DBD Local CI (DbdLocalCi.exe)

Runs the Linux checks of the dbd-presence repository on your own PC, so they cost nothing and do not wait for GitHub's machines.
Every Linux check runs in a brand-new, throw-away Ubuntu (WSL) that belongs to a Windows user of its own (dbd-ci-wsl), not to you; it is deleted
when the check ends. By design your own files, your own Linux and your own WSL settings are not used or changed by it; that has not been seen working
on a Windows PC yet (PENDING-ENV), which is why set-up includes an isolation probe that you watch and accept.
The Windows checks, the screen tests, the release build, the deploys and the backups stay on GitHub's machines (and use GitHub's included minutes).

Get the program from GitHub (Actions, CI (Windows), the run, Artifacts: local-ci-win-x64, a zip with four files) and compare the SHA-256 in the run's Summary with
"Get-FileHash .\DbdLocalCi.exe" (capital or small letters do not matter, the digits do). It is not signed, so Windows may warn: More info, Run anyway.

First time:
  0. Merge the pull request that holds this program first, so that its test workflow is on the repository's main branch. Without that, the test at the end
     of set-up cannot start its own workflow: it waits for you to push a commit and then judges a whole CI run, which takes up to about two hours.
  1. Make the GitHub token (setup shows the exact clicks; a fine-grained token that starts with github_pat_, 30 days, never "no expiration").
     The repository must be private.
  2. Double-click DbdLocalCi.exe, from any folder, and choose 1 (Set up). Windows asks for permission: choose Yes.
     The program installs itself to C:\Program Files\DbdLocalCi\ (only Administrators can change it there), keeps its files in
     C:\ProgramData\DbdLocalCi\ (only Administrators can open it) and makes the Windows user dbd-ci-wsl. Afterwards you can delete the copy you started from.
  3. If it says "Restart your PC", restart and open DbdLocalCi.exe again.
  4. At step 9 the isolation probe tries to get out of the Linux machine the way a hostile job would and shows what worked. Nothing is sent to this PC
     until you type yes. After it, you are asked whether the program may update WSL itself once a week (the answer is no unless you type yes).
     No Linux check starts on this PC unless the last probe was accepted for the current setup and the WSL that is installed now: run it again (menu 5)
     after Windows or WSL updates.
  Set-up says "Done" only when its test check ran on your PC and passed; otherwise it says what is not proven and turns the variable off again.

Afterwards just double-click DbdLocalCi.exe and choose 2 (Status). Status, 3 (Test check), 5 (Isolation probe) and uninstall open the administrator window, because
the data is closed to ordinary programs. To turn it off and remove it (the Windows user dbd-ci-wsl too) choose 4.
Running set-up again is safe, but it stops the background loop while it runs, and a check that is running on this PC at that moment is ended with it.
Other commands: DbdLocalCi.exe help.

Only if you said yes at set-up, WSL is updated for the whole PC (wsl --update) once a week, when nobody is using WSL; to stop that later, create an empty file
named no-wsl-update in C:\ProgramData\DbdLocalCi\ or run set-up again and say no. Other program names of the game, one per line, go in game-names.txt in the same folder.

Full guide: docs/owner/free-ci.md in the repository. Read "The words that matter: risks that remain" before you start.
Logs: C:\ProgramData\DbdLocalCi\logs (14 days; open that folder as an administrator).
The token is stored in an administrator-only file, encrypted for this computer, and is never written to a log. It is as strong as
administration of the repository, so delete it on GitHub if you lose the PC.
Everything that touches Windows or WSL has not been run on a Windows PC yet (PENDING-ENV).
