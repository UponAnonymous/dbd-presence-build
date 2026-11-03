-- User uploads and the storage cap (V2-S5; FR-ONL-09, FR-ONL-11, FR-ADM-09, owner decision D18, ADR-0015).
-- Forward-only: never edit an applied migration; add a new numbered file.
--
-- Storage ledger. Every object the Worker writes to R2 (pack files, user uploads, release packages from V2-S6, daily
-- audit exports) has one storage_objects row, inserted *before* the object is written. The insert is the reservation:
-- src/storage.ts inserts only while the total of storage_usage plus the new object stays at or under the hard cap
-- (8 GB), in one statement, so concurrent writers can never pass it together. The triggers below keep storage_usage
-- equal to the sum of the ledger, per area, in the same transaction as every insert and delete.
-- Objects that existed before this migration: pack files are backfilled from pack_objects; audit exports written
-- before it are not in the ledger (a few kilobytes on staging, none in production).

CREATE TABLE storage_objects (
  area       TEXT NOT NULL CHECK (area IN ('packs', 'uploads', 'releases', 'audit')),
  key        TEXT NOT NULL,                          -- the R2 object key in that area's bucket
  bytes      INTEGER NOT NULL CHECK (bytes >= 0),
  created_at INTEGER NOT NULL,
  PRIMARY KEY (area, key)
) WITHOUT ROWID;

CREATE TABLE storage_usage (
  area    TEXT PRIMARY KEY CHECK (area IN ('packs', 'uploads', 'releases', 'audit')),
  bytes   INTEGER NOT NULL DEFAULT 0,
  objects INTEGER NOT NULL DEFAULT 0
) WITHOUT ROWID;
INSERT INTO storage_usage (area, bytes, objects) VALUES ('packs', 0, 0), ('uploads', 0, 0), ('releases', 0, 0), ('audit', 0, 0);

CREATE TRIGGER storage_objects_added AFTER INSERT ON storage_objects
BEGIN
  UPDATE storage_usage SET bytes = bytes + NEW.bytes, objects = objects + 1 WHERE area = NEW.area;
END;

CREATE TRIGGER storage_objects_removed AFTER DELETE ON storage_objects
BEGIN
  UPDATE storage_usage SET bytes = bytes - OLD.bytes, objects = objects - 1 WHERE area = OLD.area;
END;

-- A pack file's ledger row goes with its pack_objects row (releaseObjects, purgeObjects in src/db/packs.ts).
CREATE TRIGGER pack_objects_removed AFTER DELETE ON pack_objects
BEGIN
  DELETE FROM storage_objects WHERE area = 'packs' AND key = OLD.sha256 || CASE OLD.format WHEN 'png' THEN '.png' ELSE '.jpg' END;
END;

INSERT INTO storage_objects (area, key, bytes, created_at)
  SELECT 'packs', sha256 || CASE format WHEN 'png' THEN '.png' ELSE '.jpg' END, size, created_at FROM pack_objects;

-- User uploads (D4, D12). One row per stored file. The file lives in R2 bucket UPLOADS under object_key,
-- "<32 lowercase hex>.<png|jpg>": 128 random bits and no user id, served unlisted as https://<cdn host>/u/<object_key>.
-- Files are never shared between users: the same bytes uploaded again by the same user return the existing row
-- (UNIQUE (user_id, sha256)), and the same bytes from another user get a file of their own, so deleting an upload
-- always makes its address stop working. Rows go with their user (ON DELETE CASCADE: "Delete my account", the
-- owner's delete and the 12-month purge); the trigger removes the ledger row in the same transaction, and the R2
-- object is deleted right after (src/uploads.ts), with the daily sweep removing any object left behind.
CREATE TABLE uploads (
  id         TEXT PRIMARY KEY,                       -- random UUID; DELETE /v1/uploads/{id}
  user_id    TEXT NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  object_key TEXT NOT NULL UNIQUE,
  sha256     TEXT NOT NULL,                          -- lowercase hex SHA-256 of the stored bytes
  format     TEXT NOT NULL CHECK (format IN ('png', 'jpeg')),
  size       INTEGER NOT NULL,
  width      INTEGER NOT NULL,
  height     INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  UNIQUE (user_id, sha256)
);

CREATE TRIGGER uploads_removed AFTER DELETE ON uploads
BEGIN
  DELETE FROM storage_objects WHERE area = 'uploads' AND key = OLD.object_key;
END;

-- The owner can block uploads for one user (FR-ADM-09); the global switch and the per-user limit are settings rows
-- (uploads_blocked, upload_limit; defaults in src/db/settings.ts).
ALTER TABLE users ADD COLUMN uploads_blocked INTEGER NOT NULL DEFAULT 0 CHECK (uploads_blocked IN (0, 1));
