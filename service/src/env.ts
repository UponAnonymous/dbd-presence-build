// Worker bindings (service/wrangler.toml). Secrets are entered by the owner with `wrangler secret put`; none is in the repository.

export interface Env {
  DB: D1Database;
  /** Official pack files, `<sha256>.<png|jpg>` (src/db/packs.ts, src/routes/cdn.ts). */
  PACKS: R2Bucket;
  /** User uploads (V2-S5). */
  UPLOADS: R2Bucket;
  /** Daily append-only audit export (src/audit-export.ts); a separate bucket with a bucket lock rule. */
  AUDIT_EXPORT: R2Bucket;
  /** Signed update packages, `<sha256>.nupkg` (V2-S6, src/routes/releases.ts): only those the published feeds list. */
  RELEASES: R2Bucket;

  ENVIRONMENT: string;
  /** Host of the public API, for example `api.arcavale.com`. */
  API_HOST: string;
  /** Host of the Owner Panel and its `/api/*`, for example `admin.arcavale.com`. */
  ADMIN_HOST: string;
  /** Host of the content files, for example `cdn.arcavale.com` (src/routes/cdn.ts). */
  CDN_HOST: string;
  /** Discord OAuth2 client (application) ID. Public, not a secret. */
  DISCORD_CLIENT_ID?: string;
  /**
   * Public keys of the owner's offline release and recovery keys (V2-S6), JSON `[{"kid", "spki", "purposes": ["releases"] |
   * ["key-replacement"]}]`, equal to TrustedServiceKeys.json. Public values in wrangler.toml; the private keys never reach the Worker.
   */
  OFFLINE_PUBLIC_KEYS?: string;

  DISCORD_CLIENT_SECRET?: string;
  /** JSON `{"current": kid, "keys": {kid: base64url(>= 32 random bytes)}}`. */
  INVITE_HMAC_KEYS?: string;
  /** base64url of >= 32 random bytes. Keys the ban list (src/bans.ts); never rotated, or every ban would lapse. */
  BAN_HMAC_KEY?: string;
  /** JSON `{"kid": "...", "pkcs8": base64url(PKCS#8 DER of an ECDSA P-256 private key)}`. */
  LICENCE_SIGNING_KEY?: string;
  CONFIG_SIGNING_KEY?: string;
  /** Signs the pack index (purpose `pack-index`) and the character roster (purpose `roster`), same form as LICENCE_SIGNING_KEY. */
  CONTENT_SIGNING_KEY?: string;
  /** Cloudflare Access application audience (AUD) tag. */
  ACCESS_AUD?: string;
  /** Cloudflare Access team domain, `https://<team>.cloudflareaccess.com`. */
  ACCESS_TEAM_DOMAIN?: string;
  /** Comma-separated owner email allow-list for the admin API. */
  OWNER_EMAILS?: string;
  /** The owner's model provider key for automatic labelling (D37, src/routes/study.ts). Unset: the relay answers 503. */
  STUDY_LABELLER_KEY?: string;
  /** The model the relay puts in place of the app's placeholder (a var in wrangler.toml, not a secret). */
  STUDY_LABELLER_MODEL?: string;
  /**
   * Where and how the relay forwards (a JSON var in wrangler.toml, not source): `{ "url": "https://...", "keyHeader": "...",
   * "headers": { ... } }`. Checked at use (parseProvider); missing or invalid: the relay answers 503.
   */
  STUDY_LABELLER_PROVIDER?: string;
}
