-- Round-2 review I2 and M1 (D35): a sighting row remembers the reporting account, so one account cannot fill the whole
-- sightings table, and the owner can clear what is pending. Rows already stored keep a NULL account (they expire in 30
-- days). The row goes with its account ("Delete my account"). Forward-only: never edit an applied migration.
ALTER TABLE roster_sightings ADD COLUMN user_id TEXT REFERENCES users (id) ON DELETE CASCADE;
