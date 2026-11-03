// Dashboard (FR-ADM-01, NFR-SVC-10, NFR-SVC-11; PLAN_V2 §7 "Dashboard (users, active this week, pending invites, recent
// uploads, storage)"): a read-only overview from GET /api/overview. People and invites, uploads, storage against the
// environment's cap as the service reports it (8 GB with the warning from 6 GB in production, 750 MB and 500 MB in
// staging; the same card as Uploads and Releases), the database size against D1's free 500 MB, and a
// short summary of key and backup status that links to Settings. Only what the service records is shown: request counts
// and daily database reads and writes are not visible to the service, and the page says so instead of showing a number.

import type { Overview } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren, routeLink } from "../dom.ts";
import { count } from "../format.ts";
import { badge, button, card, errorState, loading, notice, pageHeader, setBusy } from "../ui.ts";
import { backupFacts, facts, keyLabel, missingKeys } from "./settings.ts";
import { storageCard } from "./uploads.ts";

/** Database size in decimal megabytes, like the 500 MB free-tier figure. */
export function megabytes(bytes: number): string {
  return `${(bytes / 1_000_000).toFixed(1)} MB`;
}

function stat(label: string, value: number, hint: string | null = null): HTMLElement {
  return el("div", { className: "stat" }, el("dt", { className: "stat-label", text: label }), el("dd", { className: "stat-value", text: count(value) }), hint ? el("dd", { className: "stat-hint", text: hint }) : null);
}

function peopleCard(o: Overview): HTMLElement {
  return card(
    "People",
    el(
      "dl",
      { className: "stat-grid" },
      stat("People with access", o.users.withAccess),
      stat("Active in the last 7 days", o.users.activeThisWeek, "Opened the app while signed in"),
      stat("Waiting for an invite", o.users.waitingForInvite),
      stat("Unused invite codes", o.invites.pending, "Not redeemed, revoked or expired"),
      stat("Revoked", o.users.revoked),
    ),
    el("p", { className: "card-links" }, routeLink("#/users", "Open Users"), routeLink("#/invites", "Open Invites")),
  );
}

function uploadsCard(o: Overview): HTMLElement {
  return card(
    "Uploads",
    el("dl", { className: "stat-grid" }, stat("Pictures stored", o.uploads.total), stat("Uploaded in the last 7 days", o.uploads.thisWeek)),
    el("p", { className: "card-links" }, routeLink("#/uploads", "Open Uploads")),
  );
}

function databaseCard(o: Overview): HTMLElement {
  const { sizeBytes, freeLimitBytes } = o.database;
  const size =
    sizeBytes === null
      ? el("p", { className: "muted", text: "The database did not report its size this time. Refresh to try again." })
      : el(
          "div",
          {},
          el("p", { className: "muted", text: `${megabytes(sizeBytes)} of ${megabytes(freeLimitBytes)} used (the free size for one database).` }),
          el("meter", { className: "storage-meter", attrs: { min: 0, max: freeLimitBytes, low: freeLimitBytes * 0.75, high: freeLimitBytes * 0.75, optimum: 0, value: sizeBytes, "aria-label": "Database size of the free limit", id: "database-meter" } }),
          sizeBytes >= freeLimitBytes * 0.75 ? notice("warning", "The database is above three quarters of its free size", "The service removes old records daily. If it keeps growing, look at the audit log and uploads.") : null,
        );
  return card(
    "Database and daily limits",
    size,
    el("p", { className: "muted", text: "The service cannot see how many requests it answers or how many database reads and writes it makes each day. Check those in the Cloudflare dashboard (free: 100,000 requests a day; 5 million database rows read and 100,000 rows written a day)." }),
  );
}

function statusCard(o: Overview): HTMLElement {
  const missing = missingKeys(o.keys.service);
  const releaseMissing = !o.keys.offline.configured || o.keys.offline.release.length === 0;
  const keyLine =
    missing.length > 0
      ? el("li", {}, badge("danger", "Missing"), ` ${missing.map((k) => keyLabel(k.name)).join(", ")}`)
      : el("li", {}, badge("success", "Set up"), " Every service key is set up");
  return card(
    "Keys and backups",
    el(
      "ul",
      { className: "status-list" },
      keyLine,
      el("li", {}, releaseMissing ? badge("warning", "Not set") : badge("success", "Set up"), releaseMissing ? " No release key yet: app updates cannot be uploaded" : " Release and recovery keys"),
    ),
    facts(backupFacts(o.backup).filter(([label]) => label === "Last database backup" || label === "Last restore drill")),
    el("p", { className: "card-links" }, routeLink("#/settings", "Open Settings for details")),
  );
}

function render(container: HTMLElement, ctx: ViewContext): void {
  const body = el("div", { className: "stack" });
  const refresh = button("Refresh", { icon: "refresh" });
  replaceChildren(container, pageHeader("Dashboard", "How many people use the service, how much storage it uses, and whether its keys and backups are in order. Nothing on this page changes anything.", refresh), body);

  async function load(): Promise<void> {
    replaceChildren(body, loading("Loading the overview…"));
    setBusy(refresh, true);
    try {
      const o = await ctx.api.getOverview();
      replaceChildren(body, peopleCard(o), uploadsCard(o), storageCard(o.storage), databaseCard(o), statusCard(o));
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  refresh.addEventListener("click", () => void load());
  void load();
}

export const dashboardView: View = { render };
