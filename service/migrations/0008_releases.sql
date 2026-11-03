-- Signed updates (V2-S6; FR-ONL-10, FR-ADM-10, NFR-SVC-05, NFR-SVC-11, owner decision D18, ADR-0012).
-- Forward-only: never edit an applied migration; add a new numbered file.
--
-- The Worker never holds the release key or the recovery key. The owner signs release feeds and key replacements offline
-- (tools/DbdPresence.ReleaseSigner) and uploads them in the Owner Panel; the service checks each against the public keys in
-- OFFLINE_PUBLIC_KEYS (wrangler.toml) with the same rules as the app, and serves the signed text exactly as uploaded.

-- One row per uploaded feed. A channel has at most one staged feed (uploaded, waiting for its packages and for Publish)
-- and at most one published feed (served at GET /v1/releases/{channel}). Older rows stay as history ('superseded' or
-- 'discarded'); their document text is kept so the audit trail can show what was served.
CREATE TABLE release_feeds (
  id            TEXT PRIMARY KEY,                     -- random UUID
  channel       TEXT NOT NULL CHECK (channel IN ('stable', 'beta')),
  feed_version  INTEGER NOT NULL CHECK (feed_version >= 0),
  kid           TEXT NOT NULL,                        -- the release key that signed it
  issued_at     INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  newest        TEXT,                                 -- highest app version listed, or NULL for an empty feed
  document      TEXT NOT NULL,                        -- the signed document text exactly as uploaded (at most 64 KB)
  status        TEXT NOT NULL CHECK (status IN ('staged', 'published', 'superseded', 'discarded')),
  uploaded_at   INTEGER NOT NULL,
  uploaded_by   TEXT NOT NULL,
  published_at  INTEGER,
  published_by  TEXT
);
CREATE UNIQUE INDEX release_feeds_one_staged ON release_feeds (channel) WHERE status = 'staged';
CREATE UNIQUE INDEX release_feeds_one_published ON release_feeds (channel) WHERE status = 'published';

-- The packages a feed lists (at most 3: D18 keeps only the last 3 packages).
CREATE TABLE release_feed_packages (
  feed_id    TEXT NOT NULL REFERENCES release_feeds (id) ON DELETE CASCADE,
  sha256     TEXT NOT NULL,                           -- lowercase hex
  version    TEXT NOT NULL,
  file_name  TEXT NOT NULL,
  size       INTEGER NOT NULL CHECK (size > 0),
  PRIMARY KEY (feed_id, sha256)
) WITHOUT ROWID;

-- Stored packages, in R2 bucket RELEASES under "<sha256>.nupkg" and served at https://<cdn host>/releases/<sha256>.nupkg
-- only while their row exists. A package is accepted only when a staged or published feed lists its hash and size, and it
-- is pruned as soon as no staged or published feed lists it any more, so at most the last 3 packages per channel are kept.
-- Its ledger row (area 'releases') is reserved before the R2 write; deleting this row marks the ledger row released, and
-- the ledger row goes after the R2 delete succeeded (src/storage.ts), as for packs and uploads.
CREATE TABLE release_packages (
  sha256       TEXT PRIMARY KEY,
  size         INTEGER NOT NULL CHECK (size > 0),
  uploaded_at  INTEGER NOT NULL,
  uploaded_by  TEXT NOT NULL
) WITHOUT ROWID;

CREATE TRIGGER release_packages_removed AFTER DELETE ON release_packages
BEGIN
  UPDATE storage_objects SET released = 1 WHERE area = 'releases' AND key = OLD.sha256 || '.nupkg';
END;

-- Key replacements signed offline by the recovery key (PLAN_V2 §6.2). Each is cumulative; the newest (highest version)
-- is served at GET /v1/keys.
CREATE TABLE key_replacements (
  version      INTEGER PRIMARY KEY CHECK (version >= 0),
  kid          TEXT NOT NULL,
  document     TEXT NOT NULL,
  uploaded_at  INTEGER NOT NULL,
  uploaded_by  TEXT NOT NULL
);
