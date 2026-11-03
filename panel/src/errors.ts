// Honest error copy (UI_SPEC §5): what happened, and the next action. The service's error code is shown as-is so a
// problem can be matched to the service logs; nothing is guessed about whether a change was applied.

import { ApiError } from "./api.ts";

export interface ErrorCopy {
  title: string;
  detail: string;
  /** The one next step the error state offers. */
  action: "reload" | "retry" | "none";
}

const CLIENT_CODES: Record<string, string> = {
  not_found: "It no longer exists. It may have been deleted in another tab. Reload the list to see the current state.",
  invite_not_revocable: "This invite was already redeemed or revoked, so it can no longer be revoked.",
  already_revoked: "This was already revoked.",
  user_changed: "This user's status changed while the request was running, so nothing was deleted. Reload the list to see the current state, then try again if you still want to.",
  confirmation_mismatch: "The service did not accept the confirmation. Nothing was deleted.",
  invalid_request: "The service refused the values that were sent. Check the form and try again.",
  invalid_id: "This record has an identifier the panel does not recognise.",
  unsupported_media_type: "The service refused the request format.",
  body_too_large: "The request was too large for the service.",
  method_not_allowed: "The service does not offer this action.",
  rate_limited: "Too many requests in a short time. Wait a moment and try again.",
  pack_exists: "A pack with this ID already exists. Choose another ID.",
  fallback_exists: "Another pack is already the fallback pack. Untick it there first; only one pack can be the fallback.",
  pack_empty: "The pack has no images yet. Add at least one before publishing.",
  permission_required: "This pack is marked as made by someone else. Record the creator's permission and a credit line in its details first.",
  pack_too_large: "The pack's images would add up to more than 32 MB. Remove or shrink some images.",
  index_limits: "The pack list would go over its limits (100 packs, 4,000 images, 32 MB per pack, one default pack).",
  default_exists: "Another pack already holds the default pictures. Clear it there first; only one pack can be the default.",
  code_exists: "A character already has this code. Codes never change; use the next free code shown in the form.",
  id_exists: "Another character already uses this ID. Leave the ID empty to get a free one, or choose another.",
  name_collision: "The name is too close to another character's name: the app could not tell them apart on screen. Check for a typing error.",
  invalid_name: "Names may use letters, digits, spaces and . ' \" & : – - only, up to 80 characters, with no double spaces.",
  invalid_alias: "Every other spelling needs where it was seen on screen.",
  seed_character: "Characters the app ships with cannot be renamed or removed here; they change only with an app release.",
  roster_full: "The character list is full (400 characters).",
  roster_invalid: "The change would make a character list the app refuses, so nothing was changed.",
  reporter_limit: "Two accounts are already marked as your game accounts. Unmark one first.",
  user_not_active: "Only an active account can be marked as your game account.",
  pack_published: "The pack is published. Unpublish it first.",
  pack_not_published: "The pack is not published.",
  key_exists: "This pack already has an image for that slot. Replace that image instead.",
  file_missing: "The uploaded file is no longer stored. Upload it again.",
  not_licensed: "Only images marked as licensed can be withdrawn.",
  entry_withdrawn: "The image is withdrawn. Restore it before marking it as not licensed.",
  image_rejected: "The image was not accepted.",
  feed_refused: "The release feed was refused: it is not signed by the release key for this channel, has expired, is older than the published one, or does not follow the feed rules. Nothing was changed.",
  feed_version_not_newer: "This feed is not newer than the published one. Sign a new feed (or sign it again with the resign command) and upload that.",
  release_version_lower: "The newest version in this feed is lower than the published one. The app never installs a lower version: to roll back, pack the older build again with a higher version number and sign a new feed.",
  feed_not_staged: "This feed is no longer waiting to be published. Reload to see the current state.",
  packages_missing: "Upload every package the feed lists before publishing it.",
  package_not_in_feed: "No staged or published feed lists this package. Upload the signed feed first, then its packages.",
  package_size_mismatch: "The file's size is not the size the signed feed lists. Choose the package the feed was signed for.",
  package_not_stored: "The package was not stored: its bytes did not match the signed feed's SHA-256, or storage failed. Nothing was kept; try again with the package the feed was signed for.",
  length_required: "The upload had no size. Reload the page and try again.",
  key_replacement_refused: "The key replacement was refused: it is not signed by the recovery key, is older than the last one, leaves out a key the last one revoked (sign it with --previous), or would touch a recovery key. Nothing was changed.",
  key_replacement_not_newer: "This key replacement is not newer than the last one. Sign a new one with a higher version.",
};

const FORBIDDEN_CODES: Record<string, string> = {
  forbidden: "You are signed in to Cloudflare Access, but your email address is not on the owner list for this service.",
  admin_header_required: "The service refused the request because the panel's request header was missing.",
  bad_origin: "The service refused the request because it did not come from this panel's own address.",
  bad_fetch_site: "The service refused the request because the browser did not mark it as coming from this site.",
};

const SERVER_CODES: Record<string, string> = {
  not_configured: "The service is not fully configured yet. The owner set-up guide lists the secrets it needs.",
  access_keys_unavailable: "The service could not fetch the Cloudflare Access signing keys to check your session.",
  release_key_not_configured: "The service has no release public key yet. Make the release key with the release signer and add its public key (docs/owner/release-keys.md).",
  recovery_key_not_configured: "The service has no recovery public key yet. Make the recovery key with the release signer and add its public key (docs/owner/release-keys.md).",
  storage_full: "Storage is at its cap, so the file was not stored. Free space first (Uploads section), then try again.",
};

/** Turns any thrown value into the copy the error states show. */
export function describeError(error: unknown): ErrorCopy {
  if (!(error instanceof ApiError)) {
    return { title: "Something went wrong in the panel", detail: "The panel hit an unexpected problem. Reload the page; if it keeps happening, note what you did and check the browser console.", action: "reload" };
  }
  const code = error.code ? ` (${error.code})` : "";
  switch (error.kind) {
    case "offline":
      return error.code === "no_connection"
        ? { title: "You're offline", detail: "This device has no network connection. Nothing was sent. Reconnect, then try again.", action: "retry" }
        : { title: "The service could not be reached", detail: "The request did not reach the service, so it was not applied. Check your connection, then try again.", action: "retry" };
    case "session":
      return { title: "Your Cloudflare Access session expired", detail: "Reload the page to sign in again. Nothing was changed by this request.", action: "reload" };
    case "forbidden":
      return { title: "Access refused", detail: `${FORBIDDEN_CODES[error.code ?? ""] ?? "The service refused this request."}${code}`, action: "reload" };
    case "server":
      return {
        title: `The service had a problem (error ${error.status ?? "5xx"})`,
        detail: `${SERVER_CODES[error.code ?? ""] ?? "The service could not finish the request. Reload the list before trying again to see whether it was applied."}${code}`,
        action: "retry",
      };
    case "client":
      return { title: `The request was refused (error ${error.status ?? 400})`, detail: `${CLIENT_CODES[error.code ?? ""] ?? "The service refused the request."}${code}`, action: "retry" };
    case "unexpected":
      return {
        title: "Unexpected response",
        detail: "The service answered in a form the panel does not understand. The admin API may not be attached to this address, or the panel and the service are different versions.",
        action: "retry",
      };
  }
}

/** True when the error means every further call will fail the same way until the page is reloaded. */
export function isSessionError(error: unknown): boolean {
  return error instanceof ApiError && error.kind === "session";
}
