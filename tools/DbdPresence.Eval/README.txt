DBD Presence evaluator and labelling helper
===========================================

This is a test tool. It is not part of the DBD Presence app and is never released.

What it is for
  After you record matches with the study capture tool, this tool helps you say what happened in them, quickly,
  and later measures how well the screen detection reads your games.

Everything stays on your PC
  It reads the recordings the study tool saved in Documents\DBD Presence Study and writes small files next to them.
  Nothing is uploaded and the tool has no network code. It never starts another program.
  Those small files (and the review file below) are deleted together with the pictures, 30 days after the recording was made.

Making a review file to send (double-click DbdPresence.Eval.exe, type 2, press Enter)
  It makes one file, <date and time>-review.zip, inside the recording's folder: a few hundred small pictures, close-ups of the text
  the labels depend on, and a list. Send that one file to the developer's private session. Do not send the recording folder.
  It shows other players' names: never post it in public.
  Later the developer sends back a small labels file (dbd-presence-reviewer-labels-<date and time>.json). Save it in Downloads, run the
  tool again and press Enter: it takes the file in and asks you a few rows again, without showing the reviewer's answers. The labels
  count only after that quick spot-check.
  Without the menu: DbdPresence.Eval export <recording> [--max-mb 140]   and   DbdPresence.Eval import-labels <recording> <labels.json>

Labelling by hand (double-click DbdPresence.Eval.exe, press Enter)
  1. It finds your new recordings and, for each one, makes a web page (review.html) in the recording's folder.
  2. Double-click that page. Each row is a run of pictures. Check the screen name, tick "Looks right", tick
     "A new match starts here" on the first row of every match, and fill in your role, character and map.
     Orange boxes are only suggestions from the usual order of screens: look at the pictures before you tick.
  3. Click "Save my answers". Your browser saves a small file. Go back to the tool's window and press Enter.
  4. A day later, run the tool again. It asks you a few rows again without showing your earlier answers
     (a quick check that your labels are reliable). Same steps: open the page, save, press Enter.

Teaching the app your status icons (double-click DbdPresence.Eval.exe, type 3, press Enter)
  On the labelling page, rows that are a trial also ask, optionally, what your own icon shows (normal, hooked, down, dead).
  Choice 3 reads those answers from your practice recordings only (never the ones kept for the check) and saves small grey pictures
  of your own icon cell as status-templates.json in the app's data folder (%LOCALAPPDATA%\DBDPresence.Data). It needs at least 3 rows
  for each of the four icons, from at least 2 recordings; otherwise it says what is missing and writes nothing. Restart the app after.
  It turns nothing on: how well the pictures read the game is UNKNOWN until the check on held-out recordings.
  Without the menu: DbdPresence.Eval teach-status [--out <file>]

Measuring (for the person who runs the evaluation)
  DbdPresence.Eval manifest --out heldout-manifest.txt         lists the held-out recordings; commit it first
  DbdPresence.Eval evaluate --out report.md --manifest heldout-manifest.txt --manifest-hash <hash from the commit> --owner-name <your in-game name>
  The in-game name is only typed on the command line, is kept in memory for that run and is never written to a file.
  A held-out run refuses tuning recordings, labels that did not pass the second look, and any set that differs from the
  committed manifest. The report holds the evaluator's own numbers; nothing in it is typed by hand.
  DbdPresence.Eval evaluate --split tuning --out tuning.md     the same on the tuning recordings (for calibration, never a result)

Freeing disk space (automatic, you answer nothing)
  A sitting takes about 2 GB. The tool tidies up by itself and says in plain words what it did:
  - When a recording is prepared (the menu, choice 1 or 2, or 'prepare') it rewrites the pictures smaller without changing a single pixel
    (each one is read back and compared before the old one is replaced). About 30% smaller in the one sample of 1080p game screenshots that was measured; the tool prints what it saved on yours.
  - The review file (<date and time>-review.zip, or its pieces) is deleted when your quick spot-check of the reviewer's labels has passed.
  - After 'evaluate' has written the held-out report completely, the pictures of the held-out recordings are deleted, except the pictures of match parts
    the report lists as wrong or missed (kept so the mistake can be looked at). Labels, answers and the manifest stay, so the manifest still matches.
    'evaluate --keep-pictures' keeps them (for a repeat run). A run of only some detectors (--detector) keeps them too.
  - The pictures of the first two (practice) recordings are deleted only when the held-out report exists AND 'teach-status' has succeeded.
  Please do not delete a recording's folder or pictures by hand before the check has been run. The tool only deletes its own pictures and review files,
  never follows a link, leaves any other file alone and names anything it could not delete. It remembers that the check was run and the icons taught in
  study-state.json, next to the recordings (recording names, a hash and times only). Everything is deleted anyway 30 days after the sitting.

Privacy
  The recordings and the review pages can show other players' names. Do not share them in public.
