// Releases (FR-ADM-10, FR-ONL-10, NFR-SVC-05, D18; PLAN_V2 §6.5, §7; V2-S6): signed app updates. The panel never holds the release
// key or the recovery key. The owner signs a release feed and the update packages offline with the release signer
// (docs/owner/release-keys.md) and uploads them here: stage the signed feed for a channel, upload the packages it lists, publish.
// The service checks the feed exactly as the app does and keeps only the packages the feeds list (the last 3 per channel).
// A rollback is a new, higher version that contains the older build: the app never installs a lower version.
// Everything a file says is shown as text only; the panel decodes a feed only to preview it, and the service verifies it.

import { ApiError, type ReleaseChannel, type ReleaseFeedView, type ReleaseState } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { dateTime, plural, relative } from "../format.ts";
import { badge, button, card, cell, errorState, field, loading, mono, notice, pageHeader, setBusy, stacked, table } from "../ui.ts";
import { formatBytes } from "./packs.ts";
import { storageCard } from "./uploads.ts";

const CHANNELS: readonly { id: ReleaseChannel; label: string }[] = [
  { id: "stable", label: "Stable" },
  { id: "beta", label: "Beta" },
];
const DAY = 86_400_000;
const PACKAGE_COLUMNS = ["Version", "File", "Size", "SHA-256", "Stored"] as const;

/** What a signed document says about itself, decoded for a preview only (never trusted: the service verifies the signature). */
export interface EnvelopePreview {
  kid: string;
  purpose: string;
  channel: string;
  version: number;
  expiresAt: string;
  payload: unknown;
}

function decodeBase64Url(text: string): string | null {
  if (!/^[A-Za-z0-9_-]+$/.test(text)) return null;
  try {
    const binary = atob(text.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (text.length % 4)) % 4));
    return new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(binary, (c) => c.charCodeAt(0)));
  } catch (error) {
    if (error instanceof DOMException || error instanceof TypeError) return null;
    throw error;
  }
}

/** Reads the envelope of a signed document for the preview, or null when the text is not shaped like one. */
export function previewEnvelope(text: string): EnvelopePreview | null {
  try {
    const outer = JSON.parse(text) as { signed?: unknown };
    const decoded = typeof outer.signed === "string" ? decodeBase64Url(outer.signed) : null;
    if (decoded === null) return null;
    const e = JSON.parse(decoded) as Record<string, unknown>;
    if (typeof e["kid"] !== "string" || typeof e["purpose"] !== "string" || typeof e["channel"] !== "string" || typeof e["version"] !== "number" || typeof e["expiresAt"] !== "string") return null;
    return { kid: e["kid"], purpose: e["purpose"], channel: e["channel"], version: e["version"], expiresAt: e["expiresAt"], payload: e["payload"] };
  } catch (error) {
    if (error instanceof SyntaxError) return null;
    throw error;
  }
}

function releasesOf(payload: unknown): { version: string; size: number }[] {
  const list = (payload as { releases?: unknown } | null)?.releases;
  return Array.isArray(list) ? list.map((r) => ({ version: String((r as { version?: unknown }).version ?? "?"), size: Number((r as { size?: unknown }).size ?? 0) })) : [];
}

async function sha256Hex(bytes: Uint8Array<ArrayBuffer>): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  return [...digest].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function refusalDetail(error: unknown): string | null {
  return error instanceof ApiError && error.reason ? `Reason: ${error.reason}.` : null;
}

/** The offline release and recovery keys the service checks uploads against (public key ids only); also shown in Settings. */
export function offlineKeysCard(keys: ReleaseState["keys"]): HTMLElement {
  const missing = !keys.configured || keys.release.length === 0;
  return card(
    "Signing keys",
    el("p", { className: "muted", text: "Public keys only. The private release and recovery keys stay on your offline drive; the service checks every upload against these." }),
    el(
      "ul",
      { className: "storage-areas" },
      el("li", {}, "Release key: ", keys.release.length > 0 ? mono(keys.release.join(", ")) : badge("warning", "not set")),
      el("li", {}, "Recovery key: ", keys.recovery.length > 0 ? mono(keys.recovery.join(", ")) : badge("warning", "not set")),
      keys.revoked.length > 0 ? el("li", {}, "Revoked by the last key replacement (refused): ", mono(keys.revoked.join(", "))) : null,
    ),
    missing
      ? notice("warning", "No release key yet", "Make the release and recovery keys with the release signer and add their public keys to the service and the app (docs/owner/release-keys.md). Until then no feed can be uploaded.")
      : null,
  );
}

function render(container: HTMLElement, ctx: ViewContext): void {
  let state: ReleaseState | null = null;
  const message = el("div", { className: "inline-message" });
  const body = el("div", {});
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });

  replaceChildren(
    container,
    pageHeader(
      "Releases",
      "Signed app updates. This panel never holds the release key: sign the feed offline with the release signer, then upload the feed and its packages here and publish. Apps install an update only when its feed carries the release key's signature and its package matches the feed.",
      refresh,
    ),
    message,
    body,
  );

  function showFailure(prefix: string, error: unknown): void {
    ctx.reportError(error);
    const copy = describeError(error);
    const reason = refusalDetail(error);
    replaceChildren(message, notice("danger", `${prefix}: ${copy.title}`, reason ? `${copy.detail} ${reason}` : copy.detail, { live: true }));
  }

  async function load(): Promise<void> {
    replaceChildren(body, loading("Loading releases…"));
    setBusy(refresh, true);
    try {
      state = await ctx.api.getReleases();
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function draw(): void {
    if (!state) return;
    replaceChildren(body, offlineKeysCard(state.keys), ...CHANNELS.map((c) => channelCard(state!, c.id, c.label)), uploadFeedCard(), replacementCard(state), storageCard(state.storage));
  }

  function packageTable(feed: ReleaseFeedView, caption: string, upload: boolean): HTMLElement {
    const rows = feed.packages.map((p) =>
      el(
        "tr",
        { attrs: { "data-package": p.sha256 } },
        cell("Version", p.version),
        cell("File", mono(p.fileName)),
        cell("Size", formatBytes(p.size)),
        cell("SHA-256", mono(`${p.sha256.slice(0, 12)}…`, "subtle")),
        cell("Stored", p.present ? badge("success", "Stored") : upload ? packagePicker(feed, p.sha256, p.version) : badge("warning", "Missing")),
      ),
    );
    return table(caption, PACKAGE_COLUMNS, rows, "table-releases");
  }

  function expiryLine(feed: ReleaseFeedView): HTMLElement {
    const left = Date.parse(feed.expiresAt) - ctx.now();
    const tone = feed.expired ? badge("danger", "Expired") : left < 7 * DAY ? badge("warning", "Expires soon") : badge("success", "Valid");
    return el("p", { className: "muted" }, tone, ` Valid until ${dateTime(feed.expiresAt)} (${relative(feed.expiresAt, ctx.now())}). Sign it again with the resign command before then; apps ignore an expired feed.`);
  }

  function channelCard(s: ReleaseState, channel: ReleaseChannel, label: string): HTMLElement {
    const { published, staged } = s.channels[channel];
    const publishedPart = published
      ? el(
          "div",
          {},
          stacked(`Version ${published.newest ?? "none"} · feed ${published.feedVersion}`, `Signed by ${published.kid}; published ${published.publishedAt ? dateTime(published.publishedAt) : "–"}`),
          expiryLine(published),
          packageTable(published, `${label}: published packages`, false),
        )
      : el("p", { className: "muted", text: "Nothing is published on this channel. Apps on it keep their version." });

    let stagedPart: HTMLElement | null = null;
    if (staged) {
      const allStored = staged.packages.every((p) => p.present);
      const publish = button("Publish", { variant: "primary", attrs: { disabled: !allStored, "aria-label": `Publish the staged ${label} feed` } });
      publish.addEventListener("click", () => void publishFeed(staged, label, publish));
      const discard = button("Discard", { attrs: { "aria-label": `Discard the staged ${label} feed` } });
      discard.addEventListener("click", () => void discardFeed(staged, label, discard));
      stagedPart = el(
        "div",
        { className: "card-body" },
        notice(
          "info",
          `Staged: version ${staged.newest ?? "none"} · feed ${staged.feedVersion}`,
          allStored ? "Every package is stored. Publish to offer it to apps on this channel." : `Upload the ${plural(staged.packages.filter((p) => !p.present).length, "missing package", "missing packages")} (the .nupkg files you signed the feed for), then publish.`,
        ),
        expiryLine(staged),
        packageTable(staged, `${label}: staged packages`, true),
        el("div", { className: "form-actions" }, publish, discard),
      );
    }

    return card(
      `${label} channel`,
      publishedPart,
      stagedPart,
      el("p", { className: "muted", text: "To roll back, pack the older build again with a higher version number, sign a new feed for it and publish that. The app never installs a lower version." }),
    );
  }

  function packagePicker(feed: ReleaseFeedView, sha: string, version: string): HTMLElement {
    const input = el("input", { className: "input input-file", attrs: { type: "file", accept: ".nupkg", "aria-label": `Package file for version ${version}` } });
    input.addEventListener("change", () => void uploadPackage(feed, sha, version, input));
    return input;
  }

  async function uploadPackage(feed: ReleaseFeedView, sha: string, version: string, input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    if (!file) return;
    replaceChildren(message);
    const expected = feed.packages.find((p) => p.sha256 === sha)!;
    if (file.size !== expected.size) {
      replaceChildren(message, notice("danger", "This is not the package the feed lists", `The feed lists ${formatBytes(expected.size)} for version ${version}; this file has ${formatBytes(file.size)}. Choose ${expected.fileName}.`, { live: true }));
      input.value = "";
      return;
    }
    const bytes = new Uint8Array(await file.arrayBuffer());
    if ((await sha256Hex(bytes)) !== sha) {
      replaceChildren(message, notice("danger", "This is not the package the feed lists", `Its SHA-256 differs from the signed feed's. Choose ${expected.fileName} from the folder you signed the feed for.`, { live: true }));
      input.value = "";
      return;
    }
    input.disabled = true;
    try {
      await ctx.api.uploadReleasePackage(sha, bytes);
      ctx.announce(`Package ${version} stored.`);
      await load();
    } catch (error) {
      input.disabled = false;
      showFailure("Could not store the package", error);
    }
  }

  async function publishFeed(feed: ReleaseFeedView, label: string, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Publish version ${feed.newest ?? "none"} on ${label}?`,
      body: [
        "Apps on this channel download and install it at their next update check (within 6 hours), after checking its signature.",
        `Only the packages the published feeds list are kept; older ones are deleted now (at most ${state?.limits.maxReleases ?? 3} per channel).`,
      ],
      confirmLabel: "Publish",
    });
    if (!ok) return;
    setBusy(trigger, true, "Publishing…");
    try {
      const { pruned } = await ctx.api.publishReleaseFeed(feed.id);
      ctx.announce(pruned > 0 ? `Published. ${plural(pruned, "old package", "old packages")} deleted.` : "Published.");
      await load();
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not publish", error);
    }
  }

  async function discardFeed(feed: ReleaseFeedView, label: string, trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: `Discard the staged ${label} feed?`,
      body: ["The published feed stays as it is. Packages that only this feed lists are deleted."],
      confirmLabel: "Discard",
      danger: true,
    });
    if (!ok) return;
    setBusy(trigger, true, "Discarding…");
    try {
      await ctx.api.discardReleaseFeed(feed.id);
      ctx.announce("Staged feed discarded.");
      await load();
    } catch (error) {
      setBusy(trigger, false);
      showFailure("Could not discard", error);
    }
  }

  function uploadFeedCard(): HTMLElement {
    const select = el("select", { className: "input select", attrs: { id: "release-channel" } }, ...CHANNELS.map((c) => el("option", { text: c.label, attrs: { value: c.id } })));
    const channelField = field("release-channel", "Channel", select);
    const input = el("input", { className: "input input-file", attrs: { type: "file", accept: ".json,application/json", id: "release-feed-file" } });
    const fileField = field("release-feed-file", "Signed feed file", input, "The releases-<channel>.json file the release signer wrote (sign-feed or resign).");
    const preview = el("div", { className: "inline-message" });
    const stage = button("Stage this feed", { variant: "primary", attrs: { disabled: true } });
    let text: string | null = null;

    const check = (): void => {
      stage.disabled = true;
      replaceChildren(preview);
      if (text === null) return;
      const envelope = previewEnvelope(text);
      if (!envelope || envelope.purpose !== "releases") {
        replaceChildren(preview, notice("danger", "This is not a signed release feed", "Choose the file the release signer wrote with sign-feed or resign.", { live: true }));
        return;
      }
      if (envelope.channel !== select.value) {
        replaceChildren(preview, notice("danger", "Wrong channel", `This feed was signed for the ${envelope.channel} channel. Choose that channel or the feed for ${select.value}.`, { live: true }));
        return;
      }
      const releases = releasesOf(envelope.payload);
      replaceChildren(
        preview,
        notice(
          "info",
          `Feed ${envelope.version} for ${envelope.channel}, signed by ${envelope.kid}`,
          `${releases.length === 0 ? "No packages." : `Versions ${releases.map((r) => r.version).join(", ")}.`} Valid until ${dateTime(envelope.expiresAt)}. The service checks the signature when you stage it.`,
        ),
      );
      stage.disabled = false;
    };

    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      text = file && file.size <= 64 * 1024 ? (await file.text()).trim() : null;
      if (file && text === null) replaceChildren(preview, notice("danger", "The file is too large", "A signed feed is a few kilobytes. Choose the releases-<channel>.json file.", { live: true }));
      else check();
    });
    select.addEventListener("change", check);
    stage.addEventListener("click", async () => {
      if (text === null) return;
      setBusy(stage, true, "Checking…");
      try {
        await ctx.api.stageReleaseFeed(select.value as ReleaseChannel, text);
        ctx.announce("Feed staged. Upload its packages, then publish.");
        replaceChildren(message);
        await load();
      } catch (error) {
        setBusy(stage, false);
        showFailure("The feed was not staged", error);
      }
    });

    return card(
      "Upload a signed feed",
      el("p", { className: "muted", text: "Staging replaces a feed that is still waiting on the same channel. The published feed changes only when you publish." }),
      channelField.root,
      fileField.root,
      preview,
      el("div", { className: "form-actions" }, stage),
    );
  }

  function replacementCard(s: ReleaseState): HTMLElement {
    const input = el("input", { className: "input input-file", attrs: { type: "file", accept: ".json,application/json", id: "key-replacement-file" } });
    const fileField = field("key-replacement-file", "Key replacement file", input, "Only after a key was lost or stolen: the file the release signer wrote with sign-key-replacement, signed by the recovery key.");
    const slot = el("div", { className: "inline-message" });
    input.addEventListener("change", async () => {
      const file = input.files?.[0];
      replaceChildren(slot);
      if (!file) return;
      const text = file.size <= 64 * 1024 ? (await file.text()).trim() : "";
      const envelope = previewEnvelope(text);
      if (!envelope || envelope.purpose !== "key-replacement") {
        replaceChildren(slot, notice("danger", "This is not a key replacement", "Choose the file the release signer wrote with sign-key-replacement.", { live: true }));
        return;
      }
      const payload = envelope.payload as { revoke?: unknown[]; add?: { kid?: unknown }[] } | null;
      const revoke = Array.isArray(payload?.revoke) ? payload.revoke.map(String) : [];
      const add = Array.isArray(payload?.add) ? payload.add.map((k) => String(k.kid)) : [];
      const ok = await ctx.confirm({
        title: "Replace signing keys?",
        body: [
          `Revoked: ${revoke.length > 0 ? revoke.join(", ") : "none"}. Trusted from now on: ${add.length > 0 ? add.join(", ") : "none"}.`,
          "Every app that checks for updates stops trusting the revoked keys for good. This cannot be undone; only a later replacement can trust a new key.",
        ],
        confirmLabel: "Replace keys",
        danger: true,
        typed: { expected: "REPLACE", label: "Type REPLACE to confirm" },
      });
      if (!ok) {
        input.value = "";
        return;
      }
      try {
        const result = await ctx.api.uploadKeyReplacement(text);
        ctx.announce(`Key replacement ${result.version} uploaded.`);
        await load();
      } catch (error) {
        input.value = "";
        showFailure("The key replacement was not accepted", error);
      }
    });
    return card(
      "Key replacement (recovery)",
      s.keyReplacement
        ? el("p", { className: "muted", text: `Current replacement: version ${s.keyReplacement.version}, signed by ${s.keyReplacement.kid}, uploaded ${dateTime(s.keyReplacement.uploadedAt)}.` })
        : el("p", { className: "muted", text: "No key replacement. Apps trust the keys they were built with." }),
      fileField.root,
      slot,
    );
  }

  void load();
}

export const releasesView: View = { render };
