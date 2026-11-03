// Dates, counts and identifiers as text. Times are shown in the browser's time zone with the zone named, so an
// audit time is never ambiguous.

const DATE_TIME = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZoneName: "short" });
const DATE = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "short", year: "numeric" });
const TIME = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit", timeZoneName: "short" });
const NUMBER = new Intl.NumberFormat("en-GB");
const RELATIVE = new Intl.RelativeTimeFormat("en-GB", { numeric: "auto" });

export function dateTime(iso: string): string {
  const ms = Date.parse(iso);
  return Number.isNaN(ms) ? iso : DATE_TIME.format(ms);
}

export function date(iso: string): string {
  const ms = Date.parse(iso);
  return Number.isNaN(ms) ? iso : DATE.format(ms);
}

export function time(iso: string): string {
  const ms = Date.parse(iso);
  return Number.isNaN(ms) ? iso : TIME.format(ms);
}

export function count(value: number): string {
  return NUMBER.format(value);
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${count(bytes)} bytes`;
  if (bytes < 1024 * 1024) return `${count(Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function plural(value: number, one: string, many: string): string {
  return `${count(value)} ${value === 1 ? one : many}`;
}

/** "3 days ago", "in 2 weeks", "just now". */
export function relative(iso: string, now: number): string {
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return iso;
  const seconds = Math.round((ms - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 60) return "just now";
  const steps: [number, Intl.RelativeTimeFormatUnit][] = [
    [60, "minute"],
    [3600, "hour"],
    [86400, "day"],
    [604800, "week"],
    [2629800, "month"],
    [31557600, "year"],
  ];
  let unit: Intl.RelativeTimeFormatUnit = "minute";
  let size = 60;
  for (const [stepSize, stepUnit] of steps) {
    if (abs >= stepSize) {
      unit = stepUnit;
      size = stepSize;
    }
  }
  return RELATIVE.format(Math.round(seconds / size), unit);
}

/** The first 8 characters of an id, for display next to a name. The full id stays available in the row. */
export function shortId(id: string): string {
  return id.slice(0, 8);
}

/** Who did something, from an audit actor (`owner:<email>`, `user:<id>`, `system`), split for a two-line cell. */
export function actorParts(actor: string): { kind: string; detail: string | null } {
  if (actor.startsWith("owner:")) return { kind: "Owner", detail: actor.slice(6) };
  if (actor.startsWith("user:")) return { kind: "User", detail: shortId(actor.slice(5)) };
  if (actor === "system" || actor.startsWith("system:")) return { kind: "Service", detail: null };
  return { kind: actor, detail: null };
}

const ACTIONS: Record<string, string> = {
  "release.feed_upload": "Uploaded a signed release feed",
  "release.package_upload": "Uploaded an update package",
  "release.publish": "Published a release feed",
  "release.discard": "Discarded a staged release feed",
  "keys.replace": "Uploaded a key replacement",
  "invite.create": "Created invites",
  "invite.revoke": "Revoked an invite",
  "invite.redeem": "Redeemed an invite",
  "invite.redeem_failed": "Invite redemption refused",
  "user.revoke": "Revoked a user",
  "user.delete": "Deleted a user",
  "user.export": "Exported a user's data",
  "device.revoke": "Revoked a device",
  "account.delete": "User deleted their account",
  "settings.update": "Changed settings",
  "apps.update": "Changed the Discord applications",
  "pack.create": "Created a pack",
  "pack.update": "Edited a pack",
  "pack.delete": "Deleted a pack",
  "pack.publish": "Published a pack",
  "pack.unpublish": "Unpublished a pack",
  "pack.file_upload": "Uploaded a pack image",
  "pack.entry_add": "Added an image to a pack",
  "pack.entry_update": "Changed an image in a pack",
  "pack.entry_remove": "Removed an image from a pack",
  "pack.entry_withdraw": "Withdrew a licensed image",
  "pack.entry_restore": "Restored a licensed image",
  "pack.purge": "Removed unused pack files",
  "session.refresh_token_reused": "Refresh token reused (session ended)",
};

/** A readable name for an audit action; unknown actions are shown by their code. */
export function actionLabel(action: string): string {
  return ACTIONS[action] ?? action;
}

/**
 * A short plain description of what an audit entry's `after` values say, or null. Invite creation records the number of codes and
 * whether a note was given (`hasNote`), never the note text, so only that is shown.
 */
export function entrySummary(entry: { action: string; after: unknown }): string | null {
  if (entry.action !== "invite.create" || typeof entry.after !== "object" || entry.after === null) return null;
  const after = entry.after as Record<string, unknown>;
  const ids = Array.isArray(after["ids"]) ? after["ids"].length : 1;
  const note = after["hasNote"] === true ? "with note" : "no note";
  return `${plural(ids, "invite", "invites")}, ${note}`;
}
