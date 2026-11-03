DBD Presence study capture
==========================

This is a test tool. It is not part of the DBD Presence app and is never released.

What it is for
  The owner needs real pictures of the game to check that the screen detection is accurate.
  This tool saves pictures of the Dead by Daylight window (only that window) while you play.

What it does and does not do
  - It saves one picture of the game window every 2 seconds, plus one extra picture if you change the window size.
  - Everything stays on your PC. Nothing is uploaded and the tool has no network code.
  - For a windowed game it keeps only the game area (no title bar, no border), so the pictures are the game's own size, for example
    1920x1080. metadata.json records this as ClientAreaOnly. Older recordings have no such field.
  - It does not touch the game. It only asks Windows for a picture of the game window, the same way a screen recorder does.
  - It stops when you close the game or press Q, or when one session reaches 2 GB.

Privacy - please read
  The pictures can show other players' names and anything else on the game screen.
  Look through the folder before you send it. Send it only to the project owner, privately.
  Never post these pictures in public.
  Recordings delete themselves 30 days after they were made. Each time the tool starts, it deletes its own recordings that are
  more than 30 days old and lists them on the screen. It only ever deletes folders it made itself (a folder named like a session
  that holds the tool's own metadata.json); it never follows shortcuts or links and never touches anything else. The deletion
  date is shown when the tool starts and is written into metadata.json. Copy what you need to keep before that date.
  The labelling tool (DbdPresence.Eval) adds a few small files next to the pictures (labels, a review page); they are deleted with the folder.

Steps
  1. Extract the whole zip into a folder. Keep all the files together.
  2. Double-click DbdPresence.StudyCapture.exe, read the notes, type Y and press Enter.
     (Windows SmartScreen may warn because the tool is not signed: click More info, then Run anyway.)
     Then start Dead by Daylight. The tool notices it by itself.
  3. Play a few matches, as killer and as survivor. Please include the lobby, the loading screens and the results screens.
     Different graphics settings (windowed, borderless, fullscreen, other resolutions) are very welcome.
  4. When you are done, click the tool's window and press Q. It tells you where the pictures are.
  5. Open the session folder, look through the pictures, then zip the folder and send it to the project owner privately.

Where the pictures are
  Documents\DBD Presence Study\<date and time>\
  Each session has its own folder with frame-00001.png, frame-00002.png and so on, and a metadata.json file.
  metadata.json also holds the date the recording deletes itself (ExpiresUtc).
  metadata.json lists the resolution, whether the window is windowed or fullscreen/borderless, the display scaling (DPI),
  the Windows build, the tool version and the time of each picture. It contains no names or account details.

If something looks wrong
  - "Capture status: Blocked" or "BlackFrames": Windows delivered no picture of the game. Tell the owner which display mode
    you used (windowed, borderless or fullscreen) and keep the session folder; it helps even without pictures.
  - To delete the pictures sooner than 30 days, delete the session folder. The tool keeps nothing anywhere else.
  - If a recording cannot be deleted automatically (for example a file is open in another program), the tool says which one
    and why. Delete that folder yourself. A folder with extra files you added is left alone and reported, never emptied.
