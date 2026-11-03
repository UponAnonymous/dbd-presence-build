// Settings (FR-ADM-01, NFR-SVC-10; PLAN_V2 §7 "Settings (key status, backup status, environment)"): which deployment
// this is, whether each of the service's keys is set up (public key ids only; the service never sends key material),
// the offline release and recovery keys, what the service has recorded about backups, and a summary of the current server
// settings with where to change them. Read-only: the settings themselves are changed under Access & features and Uploads,
// so nothing here can change what apps do. A value the service has not recorded is shown as "Not recorded yet", never guessed.

import type { BackupStatus, Overview, ServiceKey, ServiceKeyName, Settings } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren, routeLink, type Child } from "../dom.ts";
import { dateTime, plural } from "../format.ts";
import { badge, card, cell, errorState, loading, mono, notice, pageHeader, table } from "../ui.ts";
import { MODES } from "./access.ts";
import { formatBytes } from "./packs.ts";
import { offlineKeysCard } from "./releases.ts";

export const NOT_RECORDED = "Not recorded yet";

const KEYS: Record<ServiceKeyName, { label: string; use: string }> = {
  licence: { label: "Licence signing key", use: "Signs the licence each app gets when its user signs in." },
  config: { label: "Configuration signing key", use: "Signs the settings every app downloads (access, switches, banner, minimum version)." },
  content: { label: "Content signing key", use: "Signs the list of packs and the list of characters." },
  invite: { label: "Invite key", use: "Checks invite codes. Older key ids stay so unused codes keep working." },
  ban: { label: "Ban key", use: "Recognises a banned person even after their account is deleted." },
};

const KEY_COLUMNS = ["Key", "What it does", "Status", "Key id"] as const;

export function keyLabel(name: ServiceKeyName): string {
  return KEYS[name].label;
}

/** The service keys that are missing or unusable. */
export function missingKeys(keys: readonly ServiceKey[]): ServiceKey[] {
  return keys.filter((k) => !k.configured);
}

/** Backup facts as label and text; a null value reads "Not recorded yet". */
export function backupFacts(backup: BackupStatus): [string, string][] {
  return [
    ["Last database backup", backup.lastExportAt === null ? NOT_RECORDED : dateTime(backup.lastExportAt)],
    ["Size of that backup", backup.lastExportBytes === null ? NOT_RECORDED : formatBytes(backup.lastExportBytes)],
    ["Last restore drill", backup.lastDrillAt === null ? NOT_RECORDED : dateTime(backup.lastDrillAt)],
    ["Drill result", backup.lastDrillOk === null ? NOT_RECORDED : backup.lastDrillOk ? "Restored correctly" : "Failed: the restore did not give back the data"],
  ];
}

/** A definition list from label and value pairs. */
export function facts(pairs: readonly [string, Child][]): HTMLElement {
  return el("dl", { className: "kv kv-facts" }, ...pairs.flatMap(([term, value]) => [el("dt", { text: term }), el("dd", {}, value)]));
}

export function backupNotice(backup: BackupStatus): HTMLElement | null {
  if (backup.lastDrillOk === false) return notice("danger", "The last restore drill failed", "A backup that cannot be restored does not protect the data. Run the drill again after fixing the problem.");
  if (backup.lastExportAt === null) return notice("warning", "No backup is recorded yet", "Backups and restore drills appear here once they have run. Until then there is no record that the data could be restored.");
  if (backup.lastDrillAt === null) return notice("warning", "No restore drill is recorded yet", "A backup only counts once a restore from it has been tried.");
  return null;
}

function environmentName(name: string): string {
  return name === "production" ? "Production" : name === "staging" ? "Staging" : name;
}

function environmentCard(o: Overview, ctx: ViewContext): HTMLElement {
  const panel = ctx.environment;
  const mismatch = o.environment !== panel;
  return card(
    "Environment",
    mismatch
      ? notice("danger", "This panel and the service disagree about where they run", `The panel is on the ${environmentName(panel).toLowerCase()} address, but the service says it is ${environmentName(o.environment).toLowerCase()}. Check the deployment before changing anything.`)
      : null,
    facts(
      [
        ["This panel", environmentName(panel)],
        ["The service says", environmentName(o.environment)],
        ["Owner Panel address", mono(o.hosts.admin)],
        ["App service address", mono(o.hosts.api)],
        ["Pictures and downloads address", mono(o.hosts.cdn)],
      ],
    ),
    el("p", { className: "muted", text: "Staging and production are fully separate: their own data, keys and sign-in. Nothing done in staging reaches production users." }),
  );
}

function keysCard(o: Overview): HTMLElement {
  const rows = o.keys.service.map((k) =>
    el(
      "tr",
      { attrs: { "data-key": k.name } },
      cell("Key", KEYS[k.name].label),
      cell("What it does", KEYS[k.name].use),
      cell("Status", k.configured ? badge("success", "Set up") : badge("danger", "Missing")),
      cell("Key id", k.kid === null ? el("span", { className: "muted", text: k.configured ? "No id" : "–" }) : mono(k.kid, "nowrap")),
    ),
  );
  const missing = missingKeys(o.keys.service);
  return card(
    "Service keys",
    el("p", { className: "muted", text: "Secrets kept by the service in Cloudflare. Only whether each one is set up and its public id are shown here; the secret itself never leaves the service." }),
    missing.length > 0
      ? notice(
          "danger",
          `${plural(missing.length, "key is", "keys are")} missing`,
          `${missing.map((k) => KEYS[k.name].label).join(", ")}. The features that need ${missing.length === 1 ? "it" : "them"} stop working until ${missing.length === 1 ? "it is" : "they are"} added again (owner guide: docs/owner/cloudflare-setup.md).`,
        )
      : notice("success", "Every service key is set up", null),
    table("Service keys", KEY_COLUMNS, rows, "table-keys"),
    o.keys.keyReplacement !== null
      ? el("p", { className: "muted", text: `Last key replacement: number ${o.keys.keyReplacement.version}, signed by ${o.keys.keyReplacement.kid}${o.keys.keyReplacement.uploadedAt ? `, uploaded ${dateTime(o.keys.keyReplacement.uploadedAt)}` : ""}.` })
      : null,
  );
}

function backupCard(o: Overview): HTMLElement {
  return card(
    "Backups",
    backupNotice(o.backup),
    facts([...backupFacts(o.backup), ["Last audit log export", o.auditExport.lastAt === null ? "Not done yet" : dateTime(o.auditExport.lastAt)]]),
    el("p", { className: "muted", text: "Cloudflare also keeps 7 days of database history (Time Travel) by itself. The service cannot see it, so it is not shown here." }),
  );
}

function settingsCard(s: Settings): HTMLElement {
  return card(
    "Current server settings",
    el("p", { className: "muted", text: "A summary only. Change them under Access & features or Uploads; every change is recorded in the audit log." }),
    facts(
      [
        ["Who can get access", MODES[s.accessMode].title],
        ["Online features", s.emergencyStop ? "Off for everyone (emergency switch)" : "On"],
        ["Screen capture switch", s.disableCapture ? "Off in every app" : "Each user decides"],
        ["Detection switch", s.disableDetection ? "Off in every app" : "Each user decides"],
        ["Minimum app version", mono(s.minimumVersion)],
        ["Devices per person", `${s.deviceLimit} (set by the service; not changed in the panel)`],
        ["New uploads", s.uploadsBlocked ? "Refused for everyone" : "Allowed"],
        ["Storage per person", formatBytes(s.uploadLimitBytes)],
        ["Pictures per person", String(s.uploadLimitFiles)],
      ],
    ),
    el("p", { className: "card-links" }, routeLink("#/access", "Open Access & features"), routeLink("#/uploads", "Open Uploads")),
  );
}

function render(container: HTMLElement, ctx: ViewContext): void {
  const body = el("div", { className: "stack" });
  replaceChildren(container, pageHeader("Settings", "Which deployment this is, whether the service's keys are set up, and what is recorded about backups. Nothing on this page changes what apps do."), body);

  async function load(): Promise<void> {
    replaceChildren(body, loading("Loading settings…"));
    try {
      const [o, state] = await Promise.all([ctx.api.getOverview(), ctx.api.getSettingsState()]);
      replaceChildren(body, environmentCard(o, ctx), keysCard(o), offlineKeysCard(o.keys.offline), backupCard(o), settingsCard(state.settings));
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    }
  }

  void load();
}

export const settingsView: View = { render };
