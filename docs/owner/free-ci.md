# Free GitHub checks on your own PC: DBD Local CI

**Status: built and tested on Linux. Everything that touches Windows, WSL or GitHub for real is PENDING-ENV: nobody has run it on a Windows PC.**
What is tested: the logic, the scripts that run inside Linux (real bash against a fake folder), the permission rules (against a model of Windows permissions) and the safety boundaries of the program.

It was reviewed four times on 2026-10-30. Each review has a "Resolution" part that says what was done with every finding:

- round 1: `docs/reviews/2026-10-30-local-ci-security-round1.md`
- round 2: `...-round2-redteam.md`, `...-round2-qa.md`, `...-round2-windows-api.md`
- round 3: `...-round3-redteam.md`, `...-round3-qa.md`
- round 4: `...-round4-redteam.md`, `...-round4-qa.md`

**The first run is done with me watching, and its first job is the isolation probe** (see "The isolation probe").
Three words are used with care on this page. **By design** means: the code does it, but nobody has seen it work on your PC. **UNVERIFIED** means: I could not check it in anything I am able to read. **UNKNOWN** means: nobody has measured it.

## What it is, in plain words

`DbdLocalCi.exe` is one program for your PC. It needs Windows 10 (build 19041 or newer) or Windows 11.
It should work on the Home edition too, because WSL 2 runs on Home. That is not tried (PENDING-ENV).

It runs the **Linux checks** of this repository on your own computer instead of on GitHub's machines.
Each check runs in a **new Linux virtual machine that is thrown away when the check ends**. It costs nothing (see "Cost").

- You set it up once. After that you do not type commands. Double-click it any time to see what is happening.
- It makes a Windows user of its own, `dbd-ci-wsl`. Everything Linux happens as that user.
  WSL belongs to a Windows user, so your own Linux, your own WSL settings and your files are not used or changed.
- WSL itself (the Windows feature) is installed once for the whole PC, so **an update of WSL changes it for you too**.
  The program updates WSL only if you say yes at set-up (see "Every day").

Why it exists: since 2026-10-28 GitHub stops every job after about 5 seconds with "The job was not started because recent account payments have failed or your spending limit needs to be increased" (D39, D40).
Jobs on your own computer do not use GitHub's included minutes.

## What runs where (your decision of 2026-10-30, "the safe mix", D41)

| Check | Where it runs |
|---|---|
| **CI (Linux)** and **CI (service)**: build, tests, the workflow checks, the service and the panel. Also the weekly run of CI (service), the pull requests of any bot you switched on (there is no Dependabot file in the repository), and the test check of this program (`local-ci-probe.yml`). | **Your PC**, in a throw-away Linux, $0 |
| **CI (Windows)**: build, tests, install checks; the screen tests; the owner tools (the release signer, this program, the evaluator, the study capture tool) | GitHub's machines, included minutes |
| **Release**, **Deploy service**, **Back up service data** (the backups run on a timer) | GitHub's machines, included minutes |

A job on your PC is only as safe as the wall around it. A Windows job would run on your own Windows install. The release, deploy and backup jobs hold keys and secrets. So these stay on GitHub.

**Never set `WINDOWS_RUNNER`, `SECRETS_LINUX_RUNNER` or `SECRETS_WINDOWS_RUNNER` yourself.** The program sets exactly one repository variable, `LINUX_RUNNER`.
If one of the three is set, set-up and Status say so, and set-up offers to delete it.

## Getting and checking the program

The program is built by GitHub, not on your PC and not on the machine that runs the repository's checks, so a job that runs on your PC cannot change it.
The job is `owner-tools` of `ci-windows.yml`: a GitHub-hosted machine, **no cache of any kind**, every package restored in locked mode from nuget.org.

1. On GitHub open the repository, **Actions**, **CI (Windows)**, and the run for the commit you want.
   A push to `main` starts a run only when it changes code or build files (`src`, `tests`, `tools`, `build` and the like). A push that changes only `docs/` does not.
   **Run workflow** starts one at any time.
2. Open the run. The **Summary** shows `DbdLocalCi.exe: SHA-256 <64 digits>, <size> bytes, built from commit <hash>`.
   Under **Artifacts** download `local-ci-win-x64`. It is a zip with four files: `DbdLocalCi.exe`, `DbdLocalCi.pdb` (debug information), `DbdLocalCi.xml` (code documentation) and `README.txt`.
3. In PowerShell run `Get-FileHash .\DbdLocalCi.exe -Algorithm SHA256`.
   The value must equal the one in the Summary. PowerShell prints capital letters and the Summary prints small ones. Capital or small does not matter; the digits do.
   The commit must be one you know.
   **PENDING-ENV:** this job has not run on GitHub yet, so the first run is its proof. The hash proves "this is what that run built from that commit", nothing more.
4. The exe is **not signed**. Windows SmartScreen may say "Windows protected your PC". Choose **More info**, then **Run anyway**.
5. **While GitHub's payment block lasts, hosted jobs do not start**, so no new exe can be made this way.
   Then the exe comes from me by hand, with its SHA-256 in another message. That is weaker (you trust me and the channel), and I will say so when I send it.
6. The program carries its own .NET runtime inside the file. **Windows Update does not update it; a new `DbdLocalCi.exe` does.**
   Status shows the day the program was built and the runtime version.

Put the exe anywhere and **run it from there. It installs itself** (set-up step 2). Afterwards you can delete the copy you started from.

## Before the first run: merge the pull request

Pull request #1 holds this program and its test workflow. **Merge it first.**
Then `local-ci-probe.yml` is on `main`, and the test at the end of set-up can start it by hand. GitHub starts a workflow by hand only when the file is on the default branch.

If you do not merge first, set-up still works, but its last step is slow and awkward:

- The test cannot start its own workflow. It waits up to 30 minutes for **you** to push a commit (see "Test check" for what to push).
- Then it follows the whole CI (Linux) run of that push. That run has 3 jobs, and they go through the one runner one after the other.
  So the test allows 15 minutes plus 30 minutes for each job: 1 hour 45 minutes for 3 jobs.
- A newer push cancels the older run. The test then says **cancelled**. That is not a failure of your PC; nothing was measured.
- If the run takes longer than its allowance, set-up says "not proven", turns `LINUX_RUNNER` off and ends with an error.

With the pull request merged the test should take a few minutes (UNKNOWN: not measured), and its limit is 30 minutes.

## Set-up (about 30 minutes plus downloads and restarts; PENDING-ENV)

1. **Make the GitHub token** (set-up shows these clicks again).
   Open `https://github.com/settings/personal-access-tokens/new`. **Token name:** `DBD Local CI`. **Expiration:** custom, **30 days** (never "No expiration").
   **Resource owner:** `UponAnonymous`. **Repository access:** only select repositories, `UponAnonymous/dbd-presence`.
   **Repository permissions:** **Administration**, **Variables** and **Actions**, each Read and write. Everything else "No access".
   Choose **Generate token** and copy it. It starts with `github_pat_`, and GitHub shows it once.
   - **A classic token (`ghp_`) is refused.** It reaches every repository of your account.
   - The program tells the two kinds apart by a header that GitHub adds only to the answers for classic tokens (`X-OAuth-Scopes`).
     **UNVERIFIED:** I could not read in GitHub's documentation that a fine-grained token never gets it. If set-up refuses a `github_pat_` token with the words "classic token", send me the message. Do not make a classic token instead.
   - The token is as strong as administration of this repository (deploy keys, protection rules, collaborators, runners). Delete it on GitHub first if you lose the PC.
   - **The repository must be private.** For a public one the program starts no runner, because anybody could open a pull request that runs on your PC.
     Set-up, Status and a daily notice say so.
2. **Run the set-up.** Double-click `DbdLocalCi.exe`, type **1**, choose **Yes** at the administrator prompt, and paste the token when asked (nothing shows while you paste).
   Use a Windows account that is an administrator.
3. **Restart if it asks** (WSL was installed). Open the program again and choose 1 again: every step checks first and skips what is done.
4. **Read and decide at step 9, the isolation probe** (next section). Nothing is sent to your PC until you type `yes`.
5. **Answer one question** after the probe: may the program update WSL itself once a week? The answer is **no** unless you type yes (see "Every day").
6. **Look at the result.** The last step runs one small test check. **Only when it ran on your PC and passed does set-up say "Done".**
   Otherwise it says what is not proven, **turns `LINUX_RUNNER` off again** (or tells you to delete it by hand) and ends with an error.

**Running set-up again is safe**, with one thing to know: it **stops the background loop while it runs, and a check that is running on this PC at that moment is ended with it.**
The loop starts again when set-up ends. If it cannot, set-up deletes `LINUX_RUNNER`, so nothing waits for a PC that does not serve it.
If you stop set-up halfway (Ctrl+C), the same happens: the loop is started again, or the variable is taken away. The variable is never left set without proof that a check ran on this PC.

The 12 steps (each can be run again and skips what is done):

1. Check Windows: build 19041 or newer, 25 GB free for a first set-up, virtualization.
2. Install the program to `C:\Program Files\DbdLocalCi\`. Only Administrators can change it there.
3. Create `C:\ProgramData\DbdLocalCi\`, for Administrators and the system only. A folder of that name that is not closed that way is renamed `DbdLocalCi.untrusted-<time>` and never trusted.
4. Make the user `dbd-ci-wsl` and its work folder, and check that WSL works **for that user**. This installs WSL (the one WSL command that runs under your administrator rights) and may ask for a restart.
5. Start and stop one Linux session as that user, to write, hold and check its 12 settings.
6. Build the base Linux image from one pinned Ubuntu 24.04.5 file (name, size and SHA-256 are in the program), with a time limit.
7. Download GitHub's runner program and check it three ways.
8. Ask for the token, check it with one request, save it, check that the repository is private, and offer to delete variables the program does not serve.
9. The isolation probe, then the question about the weekly WSL update.
10. Create the sign-in task "DBD Local CI".
11. Set `LINUX_RUNNER`. This is last, once the loop runs.
12. The test check.

**If WSL does not work for `dbd-ci-wsl`**, set-up says so in plain words.
One known cause is that WSL was installed for your own Windows user only (for example from the Microsoft Store). `wsl --install --no-distribution`, which the program runs as administrator, installs it for the whole PC.
If WSL still answers only for you, run `wsl --update` in an administrator window, restart and run set-up again. PENDING-ENV: not tried on a real PC.

## The isolation probe, in plain words (red-team round 3, H1)

**The question.** A job runs code from this repository. If somebody got bad code into it (a branch, a pull request, a dependency update), it runs on your PC, inside a throw-away Linux. The Linux is the wall.
The wall has two layers:

- Inside the Linux the job is an ordinary user (`runner`): no administrator rights, no new privileges, no user namespaces.
- Around the Linux are WSL and the Windows user `dbd-ci-wsl`, who can do little on your PC.

**The probe tests the second layer on purpose.** It plays a job that already became `root` (the all-powerful user inside the Linux) and tries to get out, with you watching.

**What it tries** (in a throw-away copy of the job image, a few minutes):

| It tries to | If it works ("POSSIBLE") it means |
|---|---|
| mount your Windows drive and list `C:\Users` | a job that became root could read what the user `dbd-ci-wsl` can read, and change what that user can write |
| write a test file into `C:\Users\Public` | files can be put on Windows from inside |
| start a Windows program (`cmd.exe /c whoami`) | a job could run Windows programs as `dbd-ci-wsl` |
| find the GPU device or the virtualization device | extra ways into the kernel are on |
| find folders shared in from outside (9p, virtiofs, network drives) | files of this PC are shared in |
| connect to the PC's own address on common ports (file sharing, remote desktop, web) | the PC's services are reachable (any program on a network could do this; the settings cannot stop it) |

Each try ends **BLOCKED** (did not work: good), **POSSIBLE** (worked: the way out is open) or **NOT TRIED** (could not be tried, so it is *not shown* to be closed).

- A Windows program that is found but gives no answer is **NOT TRIED**, not BLOCKED. Only the system refusing to run it counts as BLOCKED.
- If the probe script ends with an error code, the result is **incomplete** and cannot be accepted, even when every line said BLOCKED.
- That no Windows program can be started rests, when none is found, on WSL's interface for Windows programs not being there. That the interface is named and placed as the probe assumes is **PENDING-ENV**.

**The places the account may write to.** You also see the places on this PC where `dbd-ci-wsl` may write. That is what an escaped job could change.
The list covers the roots of the drives (fixed, removable and network) and their top-level folders, the Public folder, the Windows temp folder, the folders in Program Files and in ProgramData, and the folders of the system `PATH`.
It looks one level down and does not follow links. A folder in Program Files, in ProgramData or on the `PATH` that the account may write to is listed first, because a file placed there can be used by other programs.

- A place whose permissions cannot be read is **never** counted as "not writable". It is counted, named with the reason, and shown as **UNKNOWN**, and the result is marked **incomplete**.
  Where Windows refuses to show the permissions, the program tries again with the **backup right** of the administrator, switched on for the audit only. PENDING-ENV: whether that opens `System Volume Information` and the like is not tried.
- If every try was BLOCKED but the list is incomplete, the program says so and advises against accepting.
  You may still type `yes`; the status then shows "AuditIncomplete" and keeps the list of places it could not check.

The only thing the probe can leave behind is its test file. The elevated program looks for exactly that name and removes it (a plain file at that exact path only).
If it cannot, the file is on the "Leftovers" list, and the loop looks for it again at every start.

**Your decision.** Type `yes` (the whole word) to send the Linux checks to this PC. Anything else, Enter included, sends nothing, and the variable `LINUX_RUNNER` is taken away.
If anything is POSSIBLE or NOT TRIED the program recommends *not* saying yes. A probe that could not run cannot be accepted.
Your `yes` belongs to the result you read. If another window ran the probe while you were reading, your `yes` is not applied to the newer result: nothing is accepted, and the program says so.

Checks also run on this PC **without you**: the weekly run of CI (service) and the pull requests of any bot you switched on (Dependabot, for example). They install packages and run third-party code.

**No check starts on this PC unless the last probe was accepted for the setup as it is now and for the WSL as it is now.**

- If the settings of the Linux machine or the base image recipe changed since, the accepted probe is for an older setup: run it again (menu 5).
- If WSL changed since (you, Windows or the Store updated it), the probe is old: no check starts, you get one notice a day, and Status says PROBLEM. The program deletes nothing by itself, so checks already sent to this PC wait in GitHub's queue (up to 24 hours). Run the probe again, or delete `LINUX_RUNNER` on GitHub to send them back to GitHub's machines.
- If **the program** updated WSL (only when you allowed it), it repeats the attempts of the probe by itself, without the audit. Nothing worse than what you accepted keeps your acceptance (Status counts these renewals).
  Anything worse, or a probe that cannot run, withdraws the acceptance, deletes `LINUX_RUNNER` so checks go back to GitHub, starts no runner and gives you a notice. Run the probe (menu 5) and decide.

**Run it again** (menu **5**, or `DbdLocalCi probe`) after Windows or WSL updates. It is safe next to the running loop:

- If a check is running on this PC, or GitHub cannot be asked, the probe does not start and changes nothing.
- Otherwise it stops the loop, probes, and starts the loop again, however the probe ends.
- Only one program at a time may use the account's Linux machine (a lock file, `session.lock`, in the data folder), so a second program cannot shut the machine down under a running job.

**What it is not.** It is evidence about the WSL on your PC today, not a proof that nothing can get out. A WSL that ignores a setting in `.wslconfig` shows up here, because WSL skips keys it does not know without a message.
PENDING-ENV: no probe has run on a real PC. Status also warns when the Linux kernel is more than 180 days old.

## How it keeps your PC safe

- **A Windows user of its own, `dbd-ci-wsl`:** a standard user (no group but Users), hidden from the sign-in screen, with a random 32-character password that only the program keeps (encrypted for this computer).
  Uninstall removes it completely.
- **Every WSL command runs as that user.** It never runs with the program's administrator rights, except installing WSL and updating it if you allowed that.
  The command runs on a hidden desktop of its own that nobody can see or type into, and inside a Windows **job object**: a group that Windows ends all at once, and that has limits on programs and memory.
  After every job the program ends every program of that user and checks that none is left. If that fails, **Status says PROBLEM and you get a notice**, and no new check starts until it works.
  **Nothing runs on your desktop.**
- **A fresh virtual machine per job.** `wsl --shutdown` before and after. The account's `.wslconfig` is written, read back and checked. Then a **new Linux** is imported from the base image.
  It is **checked from the inside before the runner exists**. A failed check stops the job. The checks are:
  - not root, no sudo, user namespaces closed, no-new-privileges set;
  - no Windows drive, no way to start Windows programs, no shared mounts;
  - no GPU or virtualization device;
  - the memory, processor and swap limits really in force;
  - a machine that started minutes ago and is not the last job's.

  The Linux is deleted when the job ends.
- **The disk is bounded.** A Linux disk grows to at most 40 GB (`defaultVhdSize`). No job starts below **8 GB** free: the import of the image takes room first.
  The free room is looked at every **second**, from before the import until the job ends. Below **3 GB** free the job is stopped and its Linux removed.
  The first run's log says how much room the import took (UNKNOWN until then).
- **Only a private repository is served**, only a fine-grained token is taken, and only the jobs of the Linux lane run on the PC.
- **Commands that run with administrator rights** (set-up, status, the test check, the probe, uninstall, the loop) refuse to start while an environment variable asks the .NET runtime to load other code (startup hooks, profilers).
  Whether the administrator prompt passes your variables on is PENDING-ENV. A profiler is loaded before any check of the program can run, so the message tells you to delete the variable; it cannot undo a profiler that is already loaded.
- None of this is a perfect wall; see "The words that matter".

## Every day

- At most **one runner** waits at GitHub. It is a **just-in-time runner**: it takes exactly one job and is removed. A runner that waited 12 hours is replaced.
- **A job is stopped 120 minutes after the program sees it busy.** It asks GitHub every 20 seconds. If GitHub cannot be asked at all, the job is stopped after 14 hours.
  Every `wsl.exe` command has a time limit: 3 minutes for short ones, 90 for importing or exporting, 30 for installing or updating WSL, 90 for the image build, 15 for unpacking, 2 for the checks, and a last backstop for the run of a job.
  A program's output is read in small pieces, so a stuck or noisy program cannot hang the loop.
- The base image is rebuilt **weekly** (only while no Linux is in use), the runner program is checked daily, and the token every 6 hours.
- **WSL is updated by the program only if you said yes at set-up** (the default is no). Then it runs `wsl --update` once a week, and only when it cannot get in your way:
  no Linux of this program is up and no WSL program of anybody is running. Set-up counts as the first look, so the first update is a week away, not on the first idle moment.
  A try is written down before it starts, so one that hangs is not repeated every few minutes.
  To stop it later, create an empty file `no-wsl-update` in `C:\ProgramData\DbdLocalCi\` as administrator, or run set-up again and say no. A failure is logged and changes nothing else.
  **UNVERIFIED:** whether `wsl --update` as administrator updates the WSL that the account `dbd-ci-wsl` uses. The kernel age in Status is the only way to tell.
- **The game:** no new runner starts while `DeadByDaylight-Win64-Shipping`, `DeadByDaylight-EGS-Shipping` or `DeadByDaylight` runs.
  Add other names, one per line, to `game-names.txt` in the data folder. The weekly image build, the WSL update, notices and downloads wait too.
  A waiting runner is stopped when the game starts. A job already running is not interrupted.
  The program only looks at program *names*; it never opens or touches the game.
  A job uses up to half your memory (2 to 8 GB) and half your processors (1 to 4), so the PC may feel slower.
- Log: `C:\ProgramData\DbdLocalCi\logs\localci-<date>.log` (14 days; open as administrator). It never holds the token, the password or a runner configuration. Control characters are removed. At most 5,000 lines of one job are kept.

## Status, test check, probe, turning it off

Each of these opens the administrator window (answer Yes) and waits for Enter at the end.

- **Status** (menu **2** or `DbdLocalCi status`) says in words:
  - set up or not; task running; where the program is installed; data folder closed; account `dbd-ci-wsl` as it must be; base image; runner waiting or busy;
  - whether checks go to this PC (`LINUX_RUNNER`), and a problem line for every variable it does not serve;
  - whether the repository is private;
  - the isolation probe: when, result, accepted, how often the program renewed it, and whether it still holds for this setup and this WSL;
  - the WSL version and the age of the Linux kernel of the last job, and the note on the weekly WSL update (off, not yet due, last result);
  - leftovers (a Linux machine or folder that could not be removed, the probe's test file, a use of the Linux machine that did not end cleanly);
  - the last 10 jobs, token expiry, disk used, the program's build day and runtime, and whether GitHub currently refuses jobs (UNKNOWN until measured).
- **Test check** (menu **3**, `DbdLocalCi selftest`) starts `local-ci-probe.yml` by hand on the default branch and says where its job ran. It shows the log lines on the console while it works, so the window is never silent. It has six answers:
  1. it ran on your PC and passed (the only one that counts as proof);
  2. it passed, but on a GitHub machine (the variable is not set or the loop is not running);
  3. it ran there and failed;
  4. it **did not start at all** (a payment block looks like this; read GitHub's message on the Actions page);
  5. nothing picked it up, or no run appeared, in time;
  6. it was **cancelled** (a newer push cancels the older run of CI (Linux); nothing was measured).

  If the pull request is not merged, GitHub cannot start the test by hand (see "Before the first run"). The test then waits up to 30 minutes for a push of yours and judges that push's run of `ci-linux.yml`.
  Push a commit that changes a file **outside `docs/`**, for example a blank line at the end of `tools/DbdPresence.LocalCi/README.txt`.
  `ci-linux.yml` ignores a push that changes only files under `docs/`, and GitHub may skip a commit that changes no file at all, so `git commit --allow-empty` is not reliable.
- **Isolation probe** (menu **5**): see "The isolation probe".
- **Turn it off and remove it** (menu **4**) deletes `LINUX_RUNNER` first, so checks go back to GitHub at once. Then it removes the task, the runners with exactly this program's names, leftover Linux machines, every Linux machine of `dbd-ci-wsl`, the Windows user, the downloads, the token and `C:\Program Files\DbdLocalCi`.
  - If GitHub will not delete the variable, nothing else is removed. It prints **NOT DONE**, says what to delete by hand and exits with an error. Delete the variable on GitHub, then run it again.
    If you say you deleted it by hand, the rest is removed, but it ends **NOT FULLY DONE** with an error exit. It cannot check the variable, so check it yourself.
  - **If no token is saved**, it cannot reach GitHub at all. It says so, removes everything else, and ends **NOT FULLY DONE** with "delete `LINUX_RUNNER` on GitHub by hand".
  - Keeping the program but sending jobs back to GitHub means deleting the variable on GitHub.
- **The token runs out** after at most 30 days. Status warns 7 days before, and a notice comes once a day (never while the game runs). Make a new token (step 1) and choose 1 again. The new token is checked before the saved one is replaced.

## The WSL settings

The account has its own `C:\Users\dbd-ci-wsl\.wslconfig`, written and checked **byte for byte** (UTF-8, no byte order mark, CR LF, this order, nothing else).
**Your own `.wslconfig` is never read, written or copied.** WSL skips a key it does not know without a message, which is why the checks inside the Linux and the probe prove the effect.

| Setting | Value | What it does |
|---|---|---|
| `networkingMode` | `nat` | the ordinary translated network (`mirrored` would give the Linux the PC's network identity) |
| `localhostForwarding` | `false` | a port a job opens is not reachable from Windows as `localhost` |
| `guiApplications` | `false` | no Linux window system |
| `swap` | `0` | nothing of a job is paged to a file on your disk |
| `memory`, `processors` | half the PC's, 2 to 8 GB, 1 to 4 | the most the Linux may take |
| `nestedVirtualization` | `false` | no `/dev/kvm` (Windows 11) |
| `gpuSupport` | `false` | no GPU device |
| `hostFileSystemAccess` | `false` | WSL gives the machine no share of the Windows file system, so not even root can `mount -t drvfs` (undocumented key; an older WSL ignores it; the probe shows whether it works) |
| `defaultVhdSize` | `40GB` | the most a Linux disk may grow to (the default is 1 TB) |
| `autoProxy` | `false` | the Linux does not take your proxy settings (Windows 11) |
| `firewall` | `true` | the Windows and Hyper-V firewall rules apply to the machine (Windows 11 22H2 and later) |

## Cost

**$0 for the Linux jobs on your PC.** GitHub's billing page lists self-hosted runners as usage that "remains free independently and does not reduce the included compute usage" (<https://docs.github.com/en/billing/concepts/product-billing/github-actions>, read 2026-10-29).
GitHub announced a charge of $0.002 per minute for self-hosted runners and then **postponed** it (<https://github.blog/changelog/2025-12-16-coming-soon-simpler-pricing-and-a-better-experience-for-github-actions/>, 2025-12-16): postponed, not cancelled.

**The jobs that stay on GitHub's machines use the included minutes** (2,000 a month on the free plan; Windows minutes count double). **UNKNOWN until measured:** what each part costs.
The first run after the split shows it on GitHub's usage page, and I will write it here. While the payment block lasts none of them can start. Artifacts a job uploads keep their files for at most 7 days.

**Does GitHub's block also stop my PC? UNKNOWN until measured.** GitHub's message is about payments. Nothing I could read says whether it also stops jobs for your own runner.
The test check answers it: "did not start" means jobs may be blocked for your PC too (fix the payment, or make the repository public, which this program refuses to serve).
I will not write "works" until you send me a measured result.

## First run: what to look at

After set-up open **PowerShell as administrator**, paste these one by one, and send me everything they print (nothing is secret):

```
icacls "C:\ProgramData\DbdLocalCi"
icacls "C:\ProgramData\DbdLocalCi-wsl"
icacls "C:\Program Files\DbdLocalCi"
Get-ScheduledTask -TaskName "DBD Local CI" | Format-List TaskName, State
Get-LocalUser dbd-ci-wsl | Format-List Name, Enabled, Description
net localgroup Users
net localgroup Administrators
(Get-Service seclogon).StartType
Get-Content C:\Users\dbd-ci-wsl\.wslconfig
Get-ChildItem C:\ProgramData\DbdLocalCi-wsl\distros
wsl -l -v
```

Then choose **2** (Status) and send its text. What the design expects (tell me before you go on if it differs):

- `DbdLocalCi` lists only SYSTEM and Administrators (full control). `DbdLocalCi-wsl` lists those two and `dbd-ci-wsl`. `Program Files\DbdLocalCi` lists Users with read and execute only.
- The task is `Ready` or `Running`. `net localgroup Users` lists `dbd-ci-wsl`; `Administrators` does not. `seclogon` is not `Disabled`.
- `.wslconfig` has exactly the 12 settings above. `distros` is empty when no job runs. `wsl -l -v` shows **only your own Linux**.
- **While a job runs (Status says "busy")**, run this in the same PowerShell window: `tasklist /FI "USERNAME eq $env:COMPUTERNAME\dbd-ci-wsl"`.
  (In an old Command Prompt the same command is written `%COMPUTERNAME%`. PowerShell does not expand that, and the check would wrongly show nothing.)
  It **must list programs** (`wsl.exe`, `wslhost.exe`...). That is the positive check: the filter the program uses to end the account's programs really matches them.
  Task Manager, Details, shows `dbd-ci-wsl` for `wsl.exe`. The virtual machine's process (`vmmemWSL`) may show SYSTEM (not measured).
  **Nothing may open on your screen. If a window opens, tell me at once.**
  After the job the same `tasklist` command must print "INFO: No tasks are running which match the specified criteria."
- The log has no line "Isolation check of dbd-ci-job-... failed" and has the job's output lines ("runner: ...").
- The log has a line "Free room on the drive that holds the Linux machines: ... GB before the machine ... was imported, ... GB after." Send me those two numbers: they say what the import of the image takes (UNKNOWN until then).

Not measured (all PENDING-ENV), and what a failure looks like:

| Not measured | If it does not work you will see |
|---|---|
| `wsl.exe` works for a Windows user that has never signed in | set-up stops at step 4 ("WSL is installed but does not start for the account dbd-ci-wsl" or "Windows has no profile folder for the account yet"). Restart, run again, send me the log |
| `CreateProcessWithLogonW` starts the program suspended and its standard handles reach it | a job with no output, or "Windows would not let the program run" |
| the program joins its job object (or is already in another one) | the log says "already in another Job Object ... the user-interface limits are left off" (a warning; the job runs) or "Windows would not put the program in its Job Object, so it was not started" (the job does not run) |
| the secondary logon service is on | "The Windows service 'Secondary Logon' is switched off": set it to Manual |
| `wsl --import name folder -` and `wsl --export name -` use standard input and output; `wsl --version` and `wsl --update` work as the account | set-up stops in step 6 with WSL's own words; Status shows no WSL version |
| `wsl --list --quiet --all` exits with 0 when the account has no Linux at all | the log says "'wsl --list' as the account ended with exit code N", and no job starts |
| the account's `.wslconfig` is read when its machine starts, and **each key has its effect on your WSL** | a job stops with "Isolation check ... failed" naming `no-gpu-device`, `no-kvm`, `memory-cap`, `no-swap`... or the probe shows POSSIBLE. Run `wsl --update` and the probe again |
| the interface for Windows programs is named and placed as the probe assumes (`/run/WSL/..._interop`, `WSL_INTEROP`) | the probe says BLOCKED for "start a Windows program" while one could start; the Status "Leftovers" and the log cannot tell you. This is why the first probe is read by a person |
| the backup right opens the folders the audit could not read (`System Volume Information`) | the audit says "INCOMPLETE" with the places by name; that is allowed but you decide |
| `taskkill` ends the account's programs and answers 128 when none are left | no job starts and the log says "Programs of the Windows account dbd-ci-wsl from earlier are still running", or uninstall says it "could not end the programs of the account". Use the `tasklist` check above |
| `run.sh` of the runner reads `ACTIONS_RUNNER_INPUT_JITCONFIG` from its environment | the runner never connects; the test check says "did not start" |
| a job cannot read that variable from the programs that started it | UNKNOWN: nobody looked; if it can, the runner configuration (good for one job only) is visible to the job itself |
| the hidden-account registry value (described only in community answers) | `dbd-ci-wsl` shows on the sign-in screen, Status says the account is "not hidden from the sign-in screen (run set-up)", and **no job starts** until set-up has hidden it. Tell me if set-up cannot |
| GitHub gives a fine-grained token no `X-OAuth-Scopes` header | set-up step 8 says "GitHub says this token is a classic token" for a `github_pat_` token. Tell me; do not use a classic token |
| the 1-second disk watch and the 40 GB disk limit behave on a real drive | a job ends with "The drive that holds the Linux machines has N GB free, less than 3 GB, so the job was stopped and its Linux machine removed..." or a Linux disk grows past 40 GB |

## If something is left behind

When a job ends the program removes its Linux and checks that it is gone. It asks WSL for the list again, with states, and counts a list it cannot read, or a machine that is still installing or uninstalling, as "not gone".

The list of **leftovers** names, for each entry, exactly what it is about: a Linux machine, a folder, the probe's test file, a check that could not be made, or a use of the Linux machine that did not end cleanly.
An entry is taken off only for exactly that thing, never because its name looks similar.

If something could not be removed, **Status says "Leftovers: PROBLEM" with the names, you get one notice a day (not while the game runs), and no new job starts while a leftover Linux is registered.**

- The loop looks again at its next start. A later clean-up that works clears the entry.
- A test file of the probe on Windows is looked for again at every start of the loop and removed when it can be (that exact name, a plain file only).
- If **the Linux machine could not be shut down, or the account's programs could not be ended**, after a use, that is listed too, and the next check will not start until it works.
  The program tries again before each check. If it keeps failing, sign the account out or restart Windows.
- If it keeps coming back: restart the PC (that stops WSL), or choose **4** and then **1**.
- A non-empty folder under `C:\ProgramData\DbdLocalCi-wsl\distros\` is never deleted by the program. Delete it yourself once no Linux uses it.
- Folders named `DbdLocalCi*.untrusted-<time>` are places the program found open to ordinary users and moved aside. Look, then delete them yourself.

## The words that matter: risks that remain

None of this has been seen working on Windows (PENDING-ENV). If a job (or whoever wrote it) got out of the wall, it would be the standard user `dbd-ci-wsl`, not you and not an administrator.

| Can a Linux job on your PC ... | Answer |
|---|---|
| read your documents, desktop, browser data | **Not by design** for your own profile (another standard user cannot open it). **Yes for anything `Users` or `Everyone` can read** (public folders, other drives, shared folders) *if* it got out of its Linux. The probe and the writable-places list show what is open |
| see your screen, read your clipboard, press keys in your windows | **No, by design:** no Linux windows, a hidden desktop of its own that nobody can see or type into, a job object |
| reach your own Linux or your `.wslconfig` | **Not through WSL** (it is per Windows user). **ASSUMPTION:** the virtual machines of two Windows users probably share one virtual network, so a server you run in your own WSL might be reachable by its address. Run `wsl --shutdown` in your own account while jobs run, or accept it |
| read the token, the downloads, the log, the account's password | **No:** the data folder is closed to everyone but Administrators and the system |
| use the network | **Yes:** the internet and your home network through NAT; the PC's own services (the probe shows the ports) |
| break out through a bug in WSL or the kernel | **Theoretically.** A virtual machine is the strongest wall Windows Home has, not a perfect one. An old kernel is how it would happen. Status shows the kernel's age, WSL is updated weekly if you allowed it, and user namespaces and new privileges are closed inside |
| fill the drive or take the PC down | **Limited, not impossible:** half the memory and processors, 40 GB per disk, no start below 8 GB free, stopped below 3 GB (looked at every second), 120 minutes. A job that writes faster than the drive can be looked at could still cross the margin; UNKNOWN until measured |
| leave something on a second PC drive or in Public | **Possible if it got out:** the account may write the places the probe lists. Take them seriously if that list is long, and if it says UNKNOWN |
| study recordings and other files on this PC | **The same as your documents:** closed to the account by default, not by any special rule. A PC that runs checks of code you do not fully control is a different risk from a PC that does not (`docs/SAFETY_AND_PRIVACY.md`, R-25) |

More, in words:

- **The loop runs with administrator rights** from `C:\Program Files\DbdLocalCi\`. It refuses to start if that place or the data folder could be changed by an ordinary user, or if an environment variable asks the .NET runtime to load other code.
- **The token** is in an administrator-only file, encrypted for this computer plus a second random value. That protects it from a **job** and from **accidents**, not from a program that already runs as you with your normal rights (UNVERIFIED).
  It is as strong as repository administration: make a new one every 30 days.
- **Whoever can push a branch or open a pull request that runs workflows can make a job run any command.** Some run **without you**: the weekly schedule of CI (service) and the pull requests of any bot you switched on (they install packages and run third-party code).
  `build/check-workflows.py` catches accidents only. Keep the repository private with no other collaborators, and *Fork pull request workflows* off (Settings, Actions, General).
- **`CODEOWNERS` names you** for the program, its guard tests, the whole architecture test project, the workflows and build scripts, the shared build files (`Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.config`, `.editorconfig`, the solution) and this page.
  That helps only if *Require review from Code Owners* is switched on for `main`. That is a repository setting I cannot see: **UNVERIFIED**.
- **No job with secrets runs on your PC.** If you ever set `SECRETS_LINUX_RUNNER` yourself and one ran there, roll the Cloudflare API tokens (`docs/owner/cloudflare-setup.md`) and the `BACKUP_KEY` (`docs/owner/backups.md`).
- **Caches.** The Actions cache is shared by every job of the repository, so a job on your PC could write an entry that another job restores.
  - No job that can run on your PC uses a cache. No job of `release.yml` or `ci-windows.yml` does (that file builds the development installer, the portable build and the spike tool you download and run, and your own tools).
    The deploy and backup workflows, which hold secrets, do not restore the cache either.
  - `build/check-workflows.py` allows only four actions in those jobs (`actions/checkout`, `upload-artifact`, `setup-node`, `setup-dotnet`), refuses any input with `cache` in its name that is not `false`, and requires `package-manager-cache: false` on every `setup-node`.
    It reads local actions too. It catches accidents and sloppy changes; anybody who can push a workflow can still write one that it would not understand.
- **Not signed.** Compare the SHA-256. **Set-up trusts the file you start it from, like any installer:** a program already running as you could swap it in the seconds before it is copied.
- **The runner program may update itself** inside its throw-away Linux. The newer copy is not checked by this program.
  If the program crashes, Windows may keep a dump that could hold the token (UNVERIFIED). Look in `C:\ProgramData\Microsoft\Windows\WER` and delete dumps of `DbdLocalCi.exe`.

## Good to know

- Your PC must be on and you signed in for jobs to start. A job whose label has no runner waits in GitHub's queue (up to 24 hours). The backups, deploys and the release do not depend on your PC.
- A first set-up needs about 25 GB of free disk. A download is refused when the drive has no room or the file is larger than expected (600 MB for a runner package). A job needs 8 GB free to start.
- If the PC crashes in the middle of a job, the next start of the loop removes the leftover Linux (names of exactly this program's shape only).
- The program talks only to `api.github.com`, `github.com`, the hosts GitHub's downloads come from and `releases.ubuntu.com`, over HTTPS. It starts only `wsl.exe`, `schtasks.exe` and `taskkill.exe` by full path. A test enforces both lists (`LocalCiToolBoundaryTests`).
- Several copies of the program (the loop, Status, the probe) may run side by side. They change the state file under a lock. Only one of them at a time may use the account's Linux machine.

## Facts I checked against the official sources, and what changed because of them

| Fact | Source | Effect |
|---|---|---|
| WSL keeps one session per Windows user. The virtual machine, the registered Linux machines and `wsl --shutdown` are that user's. | Microsoft's open WSL source (`LxssUserSessionFactory.cpp`) (read 2026-10-30) | The dedicated user. Whether WSL works for a never-signed-in user is PENDING-ENV. |
| `.wslconfig` is read from that user's profile when the machine is created. `hostFileSystemAccess=false` skips the Windows file-system share. Unknown keys are skipped silently. | `WslCoreConfig.h` and Microsoft Learn "Advanced settings configuration in WSL" (read 2026-10-30) | The byte-exact 12-setting file; checks from inside the Linux. |
| `wsl --import <name> <folder> <file>` accepts `-` for standard input. `wsl --export <name> -` writes to standard output. | Microsoft Learn "Basic commands for WSL" and the WSL source (read 2026-10-30) | The image goes through standard input and output. |
| `wsl --list --quiet --all` includes machines being installed or uninstalled. `wsl --update` is machine-wide. | The same | The listing includes states and a failed listing is never an empty list; the update is gated. |
| A started command's command line may be at most 1,024 characters with `CreateProcessWithLogonW`. It needs no special privilege and cannot be called from LocalSystem. | Microsoft Learn: `CreateProcessWithLogonW` (read 2026-10-29) | Scripts go through standard input. |
| It takes the window station from `STARTUPINFO.lpDesktop`. | The same page | Each command is started suspended on a private desktop, put in its job object, then resumed. |
| Only the interactive window station can show windows or receive input. | Microsoft Learn: Window Stations (read 2026-10-29) | Each command runs on a private desktop. |
| Job object: `KILL_ON_JOB_CLOSE`, `ActiveProcessLimit`, `JobMemoryLimit`. A nested job cannot have the user-interface limits. | Microsoft Learn: `JOBOBJECT_BASIC_LIMIT_INFORMATION`, `IsProcessInJob` (read 2026-10-30) | The three limits are always set; the UI limits only when the program is in no other job. |
| `CryptProtectData` with `CRYPTPROTECT_LOCAL_MACHINE` lets any process on the computer decrypt unless an entropy value is used. | Microsoft Learn (read 2026-10-29) | Machine scope plus a random entropy value in a second administrator-only file. |
| The Ubuntu 24.04.5 WSL image is on `releases.ubuntu.com/noble/` and listed in the signed `SHA256SUMS` (gpg "Good signature" 2026-10-29; the file's SHA-256 equals the list's). | <https://releases.ubuntu.com/noble/> | One pinned file (name, size, SHA-256); no list is read at run time. |
| Runner release notes carry the checksums between `<!-- BEGIN SHA linux-x64 -->` and `<!-- END SHA linux-x64 -->`. The REST asset has a `digest` field. | <https://github.com/actions/runner>, <https://docs.github.com/en/rest/releases/assets> (read 2026-10-30) | Exactly those markers; the digest must equal the notes when GitHub gives one (not seen live: PENDING-ENV). |
| Packages are served by redirect from `github.com/actions/runner/releases/download/v<version>/`. | The same (the redirect seen 2026-10-29) | Every hop is checked; an older version than one kept is never used. |
| REST: `generate-jitconfig`; variables `POST/PATCH/DELETE`; workflow `dispatches` (404 for a file not on the default branch). The repository call has `private`; lists are paged. | <https://docs.github.com/en/rest/> pages named in the code (read 2026-10-30) | Every page of the runner list is read; "did not start" is read from the Actions page. |
| Fine-grained permissions: runners need **Administration**, variables **Variables**, dispatch and cancel **Actions**. Reading annotations is not available. | The same pages (read 2026-10-30) | The three permissions in step 1. |
| The `run` command of the runner takes only `--once`, `--jitconfig` and `--startuptype`. The configuration is read from `ACTIONS_RUNNER_INPUT_JITCONFIG`. | <https://github.com/actions/runner> `CommandSettings.cs` | The configuration goes through standard input into that variable inside the Linux, never on a command line. |
| `actions/setup-node` (the pinned version) has an input `package-manager-cache`, default true: it caches by itself when `package.json` names a package manager. | The action's `action.yml` at the pinned commit (read 2026-10-30) | Every `setup-node` step says `package-manager-cache: false`, and the workflow check requires it. |
| The token expiry is announced in `github-authentication-token-expiration`. | Not found in the documentation I could read | **UNVERIFIED.** If the header is missing, Status says "GitHub did not say when it expires". |
| A fine-grained token gets no `X-OAuth-Scopes` header, and a classic token does. | Not found in the documentation I could read | **UNVERIFIED.** The program refuses a token whose answers carry the header. If the assumption is wrong, it refuses every token: see the first-run table. |

## What was not verified (PENDING-ENV or UNKNOWN)

- Every Windows-only part:
  - `wsl.exe` for another user (first use, `--import` and `--export` through standard input and output, `--shutdown`, `--version`, `--update`);
  - the local user, its profile and sign-in entry, and the folder permissions;
  - the private desktop and the job object, and starting a program suspended through the secondary logon service;
  - the scheduled task, token and password encryption, notices, the sweep of leftovers;
  - the isolation probe and its test file, the writable-places audit with the backup right, and the 1-second disk watch.
- **That nothing ever opens on your screen**, and that the account's programs are the ones the clean-up ends. If a window opens, delete `LINUX_RUNNER` on GitHub and tell me.
- Whether the GitHub block reaches self-hosted runners, and whether the hosted jobs (including `owner-tools`) start at all while it lasts.
- That the scripts inside the Linux behave there as under the fake root in the tests (they run under real bash against fake programs; a real WSL machine can differ).
- Whether WSL names and places the interface for Windows programs as the probe assumes, whether `wsl --update` as administrator reaches the account's WSL, whether GitHub gives a fine-grained token no `X-OAuth-Scopes` header, and whether *Require review from Code Owners* is on.
- Tested on Linux with fakes and models: `tests/DbdPresence.LocalCi.Tests` (run it for the current count; Windows-only tests are skipped there and run in the Windows CI), `tests/DbdPresence.ArchitectureTests/LocalCiToolBoundaryTests*.cs`, and `build/check-workflows.py --self-test`.
