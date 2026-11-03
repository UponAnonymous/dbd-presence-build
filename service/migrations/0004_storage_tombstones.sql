-- Storage tombstones (security review 2026-10-24, M2 and L2). Forward-only: never edit an applied migration.
--
-- Before: deleting an upload or pack-file row also deleted its storage_objects row (triggers in 0003), and the R2 object
-- was deleted after. When that R2 delete failed, the object stayed in R2 while nothing counted it, so the stored total
-- could read too low.
-- Now: deleting the row only marks its ledger row `released`; the bytes stay counted. The ledger row is removed only
-- after the R2 delete succeeded (src/storage.ts deleteReleased). A released row whose delete failed is a tombstone: the
-- daily sweep retries it. So the total can read too high for a while, never too low.

ALTER TABLE storage_objects ADD COLUMN released INTEGER NOT NULL DEFAULT 0 CHECK (released IN (0, 1));

DROP TRIGGER pack_objects_removed;
CREATE TRIGGER pack_objects_removed AFTER DELETE ON pack_objects
BEGIN
  UPDATE storage_objects SET released = 1
    WHERE area = 'packs' AND key = OLD.sha256 || CASE OLD.format WHEN 'png' THEN '.png' ELSE '.jpg' END;
END;

DROP TRIGGER uploads_removed;
CREATE TRIGGER uploads_removed AFTER DELETE ON uploads
BEGIN
  UPDATE storage_objects SET released = 1 WHERE area = 'uploads' AND key = OLD.object_key;
END;
