-- Content publishing and remote configuration (V2-S4; FR-ADM-08, FR-PACK-01/03/04/05/08, FR-ONL-07/08).
-- Forward-only: never edit an applied migration; add a new numbered file.
--
-- Packs have a draft side (packs, pack_entries) that the owner edits, and a published side (published_packs,
-- published_entries) that is a copy taken when the owner presses Publish. The signed pack index is built only from
-- the published side, so an edit to a published pack goes live only at its next Publish. Each index version is a row
-- of pack_index; prev_version is UNIQUE, so two concurrent publishes cannot both succeed against the same base
-- version (the loser's whole batch rolls back and is retried, as with the audit chain).
--
-- Image files live in R2 bucket PACKS under "<sha256>.<png|jpg>", one object per pack_objects row. An object is
-- deleted when nothing in pack_entries or published_entries refers to it any more (reference counting by query).

CREATE TABLE pack_objects (
  sha256     TEXT PRIMARY KEY,                       -- lowercase hex SHA-256 of the file; also its R2 key and CDN name
  format     TEXT NOT NULL CHECK (format IN ('png', 'jpeg')),
  size       INTEGER NOT NULL,
  width      INTEGER NOT NULL,
  height     INTEGER NOT NULL,
  created_at INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE packs (
  id                  TEXT PRIMARY KEY,              -- slug [a-z0-9-]{1,64}, fixed at creation
  name                TEXT NOT NULL,
  author              TEXT NOT NULL,
  licence_note        TEXT NOT NULL,
  credit              TEXT,
  fallback            INTEGER NOT NULL DEFAULT 0 CHECK (fallback IN (0, 1)),
  behaviour_art       INTEGER NOT NULL DEFAULT 0 CHECK (behaviour_art IN (0, 1)),
  -- A pack made by someone other than the owner (for example a NightLight creator) is published only with the
  -- creator's written permission recorded here (FR-PACK-08). Kept in D1 only, never in the signed index or the audit.
  third_party         INTEGER NOT NULL DEFAULT 0 CHECK (third_party IN (0, 1)),
  permission_creator  TEXT,
  permission_date     TEXT,                          -- YYYY-MM-DD
  permission_proof    TEXT,
  status              TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'published', 'unpublished')),
  version             INTEGER NOT NULL DEFAULT 0,    -- number of times published; the index carries it (>= 1 once published)
  changed             INTEGER NOT NULL DEFAULT 0 CHECK (changed IN (0, 1)), -- edited since the last Publish
  created_at          INTEGER NOT NULL,
  updated_at          INTEGER NOT NULL,
  published_at        INTEGER
);

CREATE TABLE pack_entries (
  id              TEXT PRIMARY KEY,                  -- "e" + 24 hex characters, unique across all packs
  pack_id         TEXT NOT NULL REFERENCES packs (id) ON DELETE CASCADE,
  kind            TEXT NOT NULL CHECK (kind IN ('role', 'character', 'map', 'app')),
  key             TEXT NOT NULL,                     -- kind:slug
  sha256          TEXT NOT NULL REFERENCES pack_objects (sha256),
  licensed_holder TEXT,                              -- rights holder of licensed third-party content, else NULL
  withdrawn       INTEGER NOT NULL DEFAULT 0 CHECK (withdrawn IN (0, 1)),
  created_at      INTEGER NOT NULL,
  updated_at      INTEGER NOT NULL,
  UNIQUE (pack_id, key)
);
CREATE INDEX pack_entries_sha ON pack_entries (sha256);

CREATE TABLE published_packs (
  pack_id       TEXT PRIMARY KEY,
  name          TEXT NOT NULL,
  author        TEXT NOT NULL,
  licence_note  TEXT NOT NULL,
  credit        TEXT,
  fallback      INTEGER NOT NULL,
  behaviour_art INTEGER NOT NULL,
  version       INTEGER NOT NULL
) WITHOUT ROWID;

CREATE TABLE published_entries (
  id              TEXT PRIMARY KEY,
  pack_id         TEXT NOT NULL,
  kind            TEXT NOT NULL,
  key             TEXT NOT NULL,
  sha256          TEXT NOT NULL,
  format          TEXT NOT NULL,
  size            INTEGER NOT NULL,
  width           INTEGER NOT NULL,
  height          INTEGER NOT NULL,
  licensed_holder TEXT,
  withdrawn       INTEGER NOT NULL
) WITHOUT ROWID;
CREATE INDEX published_entries_pack ON published_entries (pack_id);
CREATE INDEX published_entries_sha ON published_entries (sha256);

-- One row per signed index version. payload is the exact JSON the document carries (the C# PackIndex format).
CREATE TABLE pack_index (
  version      INTEGER PRIMARY KEY,
  prev_version INTEGER NOT NULL UNIQUE,
  payload      TEXT NOT NULL,
  published_at INTEGER NOT NULL
);

-- The Discord applications the signed config lists (D2, FR-ONL-08). Exactly one is the default.
CREATE TABLE discord_apps (
  key            TEXT PRIMARY KEY,                   -- [a-z0-9-]{1,32}
  application_id TEXT NOT NULL UNIQUE,               -- Discord snowflake, 15 to 22 digits
  name           TEXT NOT NULL,
  description    TEXT NOT NULL,
  is_default     INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
  position       INTEGER NOT NULL
) WITHOUT ROWID;
INSERT INTO discord_apps (key, application_id, name, description, is_default, position) VALUES
  ('dbd', '1563291325107937340', 'Dead by Daylight', 'Unofficial fan-made presence — not affiliated with Behaviour Interactive', 1, 0);
