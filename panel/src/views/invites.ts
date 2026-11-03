// Invites (FR-ADM-07): create one or a batch with a note and an expiry, show each new code once, list every invite
// with its status, and revoke unused ones after confirmation.

import type { CreatedInvite, Invite, InviteStatus, User } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { count, date, dateTime, plural, relative, shortId } from "../format.ts";
import { badge, button, card, cell, emptyState, errorState, field, loading, mono, notice, pageHeader, setBusy, stacked, table, type Tone } from "../ui.ts";

const STATUS: Record<InviteStatus, { label: string; tone: Tone }> = {
  active: { label: "Unused", tone: "info" },
  redeemed: { label: "Redeemed", tone: "success" },
  expired: { label: "Expired", tone: "neutral" },
  revoked: { label: "Revoked", tone: "danger" },
};

const FILTERS: readonly { id: "all" | InviteStatus; label: string }[] = [
  { id: "all", label: "All" },
  { id: "active", label: "Unused" },
  { id: "redeemed", label: "Redeemed" },
  { id: "expired", label: "Expired" },
  { id: "revoked", label: "Revoked" },
];

const EXPIRY_DAYS = [1, 7, 14, 30, 60, 90] as const;
const NOTE_MAX = 200;
const BATCH_MAX = 100;

interface State {
  invites: Invite[] | null;
  users: Map<string, User>;
  filter: "all" | InviteStatus;
}

function render(container: HTMLElement, ctx: ViewContext): void {
  const state: State = { invites: null, users: new Map(), filter: "all" };
  const listBody = el("div", { className: "card-body" });
  const listMessage = el("div", { className: "inline-message" });
  const codesSlot = el("div", { className: "codes-slot" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });

  replaceChildren(
    container,
    pageHeader("Invites", "Create invite codes and see what happened to each one. A code is shown only once, when it is created.", refresh),
    createCard(ctx, codesSlot, () => void load()),
    codesSlot,
    card("All invites", listMessage, listBody),
  );

  async function load(): Promise<void> {
    replaceChildren(listMessage);
    replaceChildren(listBody, loading("Loading invites…"));
    setBusy(refresh, true);
    const [invites, users] = await Promise.allSettled([ctx.api.listInvites(), ctx.api.listUsers()]);
    setBusy(refresh, false);
    if (invites.status === "rejected") {
      ctx.reportError(invites.reason);
      replaceChildren(listBody, errorState(invites.reason, () => void load(), ctx.reload));
      return;
    }
    state.invites = invites.value;
    state.users = new Map(users.status === "fulfilled" ? users.value.map((u) => [u.id, u]) : []);
    if (users.status === "rejected") {
      replaceChildren(listMessage, notice("warning", "Names of redeeming users could not be loaded", `${describeError(users.reason).title}. Users are shown by their ID.`));
    }
    drawList();
  }

  function drawList(): void {
    const invites = state.invites ?? [];
    if (invites.length === 0) {
      replaceChildren(listBody, emptyState("No invites yet", "Create your first invite above. Its code appears once, ready to copy and send."));
      return;
    }
    const counts = new Map<string, number>([["all", invites.length]]);
    for (const invite of invites) counts.set(invite.status, (counts.get(invite.status) ?? 0) + 1);

    const filterBar = el(
      "div",
      { className: "filter-bar", attrs: { role: "group", "aria-label": "Show invites" } },
      ...FILTERS.map((f) =>
        button(`${f.label} ${count(counts.get(f.id) ?? 0)}`, {
          variant: "ghost",
          small: true,
          attrs: { "aria-pressed": state.filter === f.id ? "true" : "false", "data-filter": f.id },
          onClick: () => {
            state.filter = f.id;
            drawList();
            container.querySelector<HTMLButtonElement>(`[data-filter="${f.id}"]`)?.focus();
          },
        }),
      ),
    );

    const shown = state.filter === "all" ? invites : invites.filter((i) => i.status === state.filter);
    const content =
      shown.length === 0
        ? emptyState(`No ${FILTERS.find((f) => f.id === state.filter)!.label.toLowerCase()} invites`, "Choose another filter to see the rest.")
        : table(
            "Invites, newest first",
            ["Status", "Note", "Created", "Expires", "Redeemed or revoked", "Actions"],
            shown.map((invite) => inviteRow(invite)),
            "table-invites",
          );
    replaceChildren(listBody, filterBar, content);
  }

  function redeemedCell(invite: Invite): HTMLElement | string {
    if (invite.status === "redeemed" && invite.redeemedAt) {
      const user = invite.redeemedBy ? state.users.get(invite.redeemedBy) : undefined;
      const who = user ? user.displayName : invite.redeemedBy ? `Deleted or unknown user ${shortId(invite.redeemedBy)}` : "Deleted user";
      return stacked(who, `Redeemed ${dateTime(invite.redeemedAt)}`);
    }
    if (invite.status === "revoked" && invite.revokedAt) return stacked("Revoked", dateTime(invite.revokedAt));
    return el("span", { className: "muted", text: "Not redeemed" });
  }

  function inviteRow(invite: Invite): HTMLTableRowElement {
    const status = STATUS[invite.status];
    const name = invite.note ?? `invite ${shortId(invite.id)}`;
    const actions =
      invite.status === "active"
        ? button("Revoke", { variant: "danger", small: true, attrs: { "aria-label": `Revoke ${name}` }, onClick: (e) => void revoke(invite, e.currentTarget as HTMLButtonElement) })
        : el("span", { className: "muted", text: "No actions" });
    return el(
      "tr",
      {},
      cell("Status", badge(status.tone, status.label)),
      cell("Note", stacked(invite.note ? invite.note : el("span", { className: "muted", text: "No note" }), mono(`ID ${shortId(invite.id)}`, "subtle"))),
      cell("Created", stacked(date(invite.createdAt), relative(invite.createdAt, ctx.now()))),
      cell("Expires", stacked(date(invite.expiresAt), invite.status === "active" || invite.status === "expired" ? relative(invite.expiresAt, ctx.now()) : null)),
      cell("Redeemed or revoked", redeemedCell(invite)),
      cell("Actions", actions),
    );
  }

  async function revoke(invite: Invite, trigger: HTMLButtonElement): Promise<void> {
    const body = ["The code stops working immediately. Anyone who has it can no longer use it to get access. This cannot be undone."];
    body.push(invite.note ? `Note: ${invite.note}` : `Invite ID: ${invite.id}`);
    const ok = await ctx.confirm({ title: "Revoke this invite?", body, confirmLabel: "Revoke invite", danger: true });
    if (!ok) return;
    setBusy(trigger, true, "Revoking…");
    try {
      const updated = await ctx.api.revokeInvite(invite.id);
      state.invites = (state.invites ?? []).map((i) => (i.id === updated.id ? updated : i));
      drawList();
      replaceChildren(listMessage);
      ctx.announce("Invite revoked.");
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      const copy = describeError(error);
      replaceChildren(listMessage, notice("danger", `Could not revoke the invite: ${copy.title}`, copy.detail, { live: true }));
    }
  }

  void load();
}

/** The create form. New codes are drawn into `codesSlot` and stay until the owner dismisses them. */
function createCard(ctx: ViewContext, codesSlot: HTMLElement, onCreated: () => void): HTMLElement {
  const countInput = el("input", { className: "input input-narrow", attrs: { type: "number", min: 1, max: BATCH_MAX, step: 1, value: 1, inputmode: "numeric", required: true } });
  const expirySelect = el("select", { className: "input select" }, ...EXPIRY_DAYS.map((d) => el("option", { text: plural(d, "day", "days"), attrs: { value: d, selected: d === 30 } })));
  const noteInput = el("input", { className: "input", attrs: { type: "text", maxlength: NOTE_MAX, autocomplete: "off", spellcheck: "true" } });
  const countField = field("invite-count", "Number of codes", countInput, `1 to ${BATCH_MAX}.`);
  const expiryField = field("invite-expiry", "Valid for", expirySelect, "Unused codes stop working after this.");
  const noteField = field("invite-note", "Note (optional)", noteInput, `Only you see it, for example who the codes are for. Up to ${NOTE_MAX} characters.`);
  const submit = button("Create invite", { variant: "primary", type: "submit" });
  const formMessage = el("div", { className: "inline-message" });

  const updateLabel = (): void => {
    const n = Number(countInput.value);
    const label = submit.querySelector(".btn-label");
    if (label && !submit.disabled) label.textContent = Number.isInteger(n) && n > 1 && n <= BATCH_MAX ? `Create ${count(n)} invites` : "Create invite";
  };
  countInput.addEventListener("input", updateLabel);

  const form = el(
    "form",
    { className: "form-grid", attrs: { novalidate: true, "aria-label": "Create invites" } },
    countField.root,
    expiryField.root,
    el("div", { className: "field-span" }, noteField.root),
    el("div", { className: "form-actions field-span" }, submit),
  );

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    void create();
  });

  async function create(): Promise<void> {
    replaceChildren(formMessage);
    countField.error.textContent = "";
    noteField.error.textContent = "";
    const n = Number(countInput.value);
    const note = noteInput.value.trim();
    let invalid = false;
    if (!Number.isInteger(n) || n < 1 || n > BATCH_MAX) {
      countField.error.textContent = `Enter a whole number from 1 to ${BATCH_MAX}.`;
      countInput.setAttribute("aria-invalid", "true");
      invalid = true;
    } else countInput.removeAttribute("aria-invalid");
    if (note.length > NOTE_MAX) {
      noteField.error.textContent = `Shorten the note to ${NOTE_MAX} characters or fewer.`;
      invalid = true;
    }
    if (invalid) {
      (countInput.getAttribute("aria-invalid") ? countInput : noteInput).focus();
      return;
    }

    setBusy(submit, true, n > 1 ? `Creating ${count(n)} invites…` : "Creating invite…");
    try {
      const created = await ctx.api.createInvites({ count: n, note: note === "" ? null : note, expiresInDays: Number(expirySelect.value) });
      setBusy(submit, false);
      updateLabel();
      noteInput.value = "";
      showCodes(ctx, codesSlot, created);
      ctx.announce(created.length === 1 ? "Invite created. Copy the code now; it is shown only once." : `${created.length} invites created. Copy the codes now; they are shown only once.`);
      onCreated();
    } catch (error) {
      setBusy(submit, false);
      updateLabel();
      ctx.reportError(error);
      const copy = describeError(error);
      replaceChildren(formMessage, notice("danger", `No invites were created: ${copy.title}`, copy.detail, { live: true }));
    }
  }

  return card("Create invites", form, formMessage);
}

function showCodes(ctx: ViewContext, slot: HTMLElement, created: CreatedInvite[]): void {
  const copied = new Set<string>();
  ctx.setLeaveGuard(true);

  const status = el("p", { className: "codes-status", attrs: { role: "status" } });
  const copyOne = async (code: string, trigger: HTMLButtonElement): Promise<void> => {
    if (await ctx.copy(code)) {
      copied.add(code);
      trigger.querySelector(".btn-label")!.textContent = "Copied";
      status.textContent = `Copied ${code}.`;
    } else {
      status.textContent = "The browser did not allow copying. Select the code and copy it by hand.";
    }
  };

  const items = created.map((invite) =>
    el(
      "li",
      { className: "code-item" },
      el("code", { className: "mono code-value", text: invite.code }),
      button("Copy", { small: true, icon: "copy", attrs: { "aria-label": `Copy code ${invite.code}` }, onClick: (e) => void copyOne(invite.code, e.currentTarget as HTMLButtonElement) }),
    ),
  );

  const copyAll = button(created.length === 1 ? "Copy code" : "Copy all codes", {
    icon: "copy",
    onClick: async () => {
      if (await ctx.copy(created.map((c) => c.code).join("\n"))) {
        for (const c of created) copied.add(c.code);
        status.textContent = created.length === 1 ? "Code copied." : `All ${created.length} codes copied, one per line.`;
      } else {
        status.textContent = "The browser did not allow copying. Select the codes and copy them by hand.";
      }
    },
  });

  const done = button("Done", {
    variant: "primary",
    onClick: async () => {
      const missing = created.length - copied.size;
      if (missing > 0) {
        const ok = await ctx.confirm({
          title: "Hide the new codes?",
          body: [
            missing === created.length ? "You have not copied any of these codes." : `You have not copied ${plural(missing, "code", "codes")}.`,
            "Codes cannot be shown again: the service keeps only a fingerprint of each one. A lost code can only be revoked and replaced.",
          ],
          confirmLabel: "Hide codes",
        });
        if (!ok) return;
      }
      replaceChildren(slot);
      ctx.setLeaveGuard(false);
      ctx.announce("New codes hidden.");
    },
  });

  const first = created[0];
  const meta = first ? [`Valid until ${dateTime(first.expiresAt)}.`, first.note ? ` Note: ${first.note}` : ""].join("") : "";
  const region = el(
    "section",
    { className: "card card-codes", attrs: { "aria-labelledby": "new-codes-title" } },
    el("h2", { className: "card-title", text: created.length === 1 ? "New invite code" : `${created.length} new invite codes`, attrs: { id: "new-codes-title", tabindex: -1 } }),
    notice("warning", "Copy now: shown only once", "The service keeps only a fingerprint of each code, so it cannot show them again after you leave this page."),
    el("p", { className: "muted", text: meta }),
    el("ul", { className: "code-list" }, ...items),
    status,
    el("div", { className: "form-actions" }, copyAll, done),
  );
  replaceChildren(slot, region);
  region.querySelector<HTMLElement>("#new-codes-title")?.focus();
}

export const invitesView: View = { render };
