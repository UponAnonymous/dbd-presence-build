# dbd-presence
An intelligent Discord Rich Presence companion for Dead by Daylight, featuring automatic gameplay detection and live match tracking.

## Build tools

The icon generator needs Pillow (`pip install pillow`). It is a developer tool only and is never shipped. It is deterministic: running it twice gives byte-identical files.

- `python3 build/generate-icons.py` writes the app icon set (`src/DbdPresence.App/Assets/*.ico`).
