-- Per-person picture quota (S5b-6; owner decision D28, FR-ONL-15, ADR-0015 addendum 3). Forward-only: never edit an
-- applied migration; add a new numbered file.
--
-- Each person may store up to `upload_limit_bytes` (60 MB by default) in at most `upload_limit_files` (300) pictures.
-- Both are checked in the same single statement that reserves the bytes in the storage ledger, before the file is
-- written to R2 (src/storage.ts reserveUpload), so parallel uploads of one person can never pass the cap together.
-- For that, an upload's ledger row names its owner. A removed upload (the user's delete, the owner's takedown, account
-- deletion) gives its owner's quota back at once: the trigger clears the owner as it marks the row released, while the
-- bytes stay counted in the service-wide total until the R2 delete succeeded (migration 0004).

ALTER TABLE storage_objects ADD COLUMN owner TEXT;  -- the uploader's user id, for area 'uploads' while the upload exists; else null

UPDATE storage_objects SET owner = (SELECT u.user_id FROM uploads u WHERE u.object_key = storage_objects.key)
  WHERE area = 'uploads' AND released = 0;

CREATE INDEX storage_objects_owner ON storage_objects (owner) WHERE owner IS NOT NULL;

DROP TRIGGER uploads_removed;
CREATE TRIGGER uploads_removed AFTER DELETE ON uploads
BEGIN
  UPDATE storage_objects SET released = 1, owner = NULL WHERE area = 'uploads' AND key = OLD.object_key;
END;

-- The per-user file limit `upload_limit` (20 by default) is replaced by the byte cap and the file backstop above. A value
-- the owner had stored for it is dropped, so the new defaults apply (src/db/settings.ts).
DELETE FROM settings WHERE key = 'upload_limit';
