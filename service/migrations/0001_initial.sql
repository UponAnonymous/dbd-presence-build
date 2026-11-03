-- Initial schema (V2-S3). Migrations are forward-only from the first deployment: never edit an applied file; add a new
-- numbered one. (Before any database existed, the security review of 2026-10-24 was folded into this file.)
-- Times are milliseconds since the Unix epoch. Secrets and tokens are stored only as hashes.
-- Indexes are kept to the ones lookups need: every index row counts against the D1 free tier's daily writes, and the
-- small tables (users, audit, counters) are cheap to scan for the daily retention job.
-- No email address and no IP history is stored (PLAN_V2 §3.1, §4.3).

CREATE TABLE users (
  id                TEXT PRIMARY KEY,               -- random UUID; the only user reference used in the audit log
  discord_id        TEXT NOT NULL UNIQUE,           -- from Discord's /users/@me, never from the client
  display_name      TEXT NOT NULL,
  status            TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
  access_granted_at INTEGER,                        -- NULL until an invite is redeemed (or sign-in in public mode)
  access_via        TEXT CHECK (access_via IN ('invite', 'public')),
  created_at        INTEGER NOT NULL,
  last_seen_at      INTEGER NOT NULL,
  revoked_at        INTEGER
);

CREATE TABLE devices (
  id             TEXT PRIMARY KEY,
  user_id        TEXT NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  public_key_jwk TEXT NOT NULL,                     -- canonical RFC 7638 member set, public key only
  thumbprint     TEXT NOT NULL,                     -- RFC 7638 SHA-256 thumbprint, base64url
  created_at     INTEGER NOT NULL,
  last_seen_at   INTEGER NOT NULL,
  revoked_at     INTEGER,
  UNIQUE (user_id, thumbprint)
);

-- One token family per device session. The access token hash lives on the family (rotated with each refresh).
CREATE TABLE token_families (
  id                TEXT PRIMARY KEY,
  user_id           TEXT NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  device_id         TEXT NOT NULL REFERENCES devices (id) ON DELETE CASCADE,
  access_token_hash TEXT NOT NULL UNIQUE,
  access_expires_at INTEGER NOT NULL,
  created_at        INTEGER NOT NULL,
  revoked_at        INTEGER,
  revoked_reason    TEXT
);
CREATE INDEX token_families_device ON token_families (device_id);

CREATE TABLE refresh_tokens (
  token_hash  TEXT PRIMARY KEY,                     -- base64url SHA-256 of the token
  family_id   TEXT NOT NULL REFERENCES token_families (id) ON DELETE CASCADE,
  created_at  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL,
  used_at     INTEGER,                              -- set on rotation; presenting a used token revokes the family
  replaced_by TEXT
) WITHOUT ROWID;
CREATE INDEX refresh_tokens_family ON refresh_tokens (family_id);

-- Single-use nonces of refresh proofs, kept for the clock-skew window.
CREATE TABLE proof_nonces (
  nonce      TEXT PRIMARY KEY,
  expires_at INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE invites (
  id          TEXT PRIMARY KEY,
  hmac_kid    TEXT NOT NULL,                        -- key id of INVITE_HMAC_KEYS used for code_hash (rotation)
  code_hash   TEXT NOT NULL,                        -- HMAC-SHA-256 of the normalised code; the code itself is never stored
  note        TEXT,
  created_at  INTEGER NOT NULL,
  created_by  TEXT NOT NULL,
  expires_at  INTEGER NOT NULL,
  redeemed_by TEXT,                                 -- users.id (kept as a pseudonym if the user is deleted)
  redeemed_at INTEGER,
  revoked_at  INTEGER,
  UNIQUE (hmac_kid, code_hash)
);

-- Rate-limit counters for the sensitive routes only; bucket names hold keyed daily hashes, never a clear IP.
CREATE TABLE rate_limits (
  bucket       TEXT NOT NULL,
  window_start INTEGER NOT NULL,
  count        INTEGER NOT NULL,
  PRIMARY KEY (bucket, window_start)
) WITHOUT ROWID;

-- Hash-chained audit log (src/db/audit.ts): hash = SHA-256 over [prev_hash, at, actor, action, target, salt,
-- SHA-256(salt|before_json), SHA-256(salt|after_json)]. The per-row salt (16 random bytes, hex) never leaves D1, so the
-- daily export's digests and hashes cannot be brute-forced back into payloads. prev_hash is UNIQUE so concurrent
-- writers cannot fork the chain.
CREATE TABLE audit_log (
  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
  at          INTEGER NOT NULL,
  actor       TEXT NOT NULL,
  action      TEXT NOT NULL,
  target      TEXT,
  before_json TEXT,
  after_json  TEXT,
  prev_hash   TEXT NOT NULL UNIQUE,
  hash        TEXT NOT NULL,
  salt        TEXT NOT NULL
);

-- Where the daily append-only audit export (src/audit-export.ts) has got to: the newest exported row and its hash.
-- The retention job never deletes that row, so a missing or changed head row means the newest rows were truncated.
CREATE TABLE audit_export_state (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  last_seq    INTEGER NOT NULL,
  last_hash   TEXT NOT NULL,
  exported_at INTEGER NOT NULL
);

-- Bans that survive account deletion (ADR-0011). Only a keyed hash of the Discord user id is kept, never the id:
-- subject_hash = HMAC-SHA-256 under the BAN_HMAC_KEY secret (its own key, never rotated) of "dbdp-ban-v1|" + the id.
-- Written when a revoked account is deleted (by the owner or by the 12-month purge); checked at every sign-in.
CREATE TABLE banned_subjects (
  subject_hash TEXT PRIMARY KEY,
  created_at   INTEGER NOT NULL
) WITHOUT ROWID;

-- Server settings as JSON values. Defaults live in code (src/db/settings.ts); these rows make them explicit.
CREATE TABLE settings (
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);
INSERT INTO settings (key, value, updated_at) VALUES ('access_mode', '"invite-only"', 0), ('device_limit', '3', 0);
