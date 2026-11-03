# Backups of the online service

This guide is for the owner. It sets up the daily encrypted backups of the service's database, runs the restore drill
once, and explains how to restore for real if that is ever needed. It follows PLAN_V2 §6 and NFR-SVC-10 (gate V7.2).

## What is backed up, and for how long

The service's **database** holds the accounts, devices, invites, settings, the pack list, the release list and the audit
log. It has two kinds of backup:

| Kind | Kept for | Who makes it | Use it for |
| --- | --- | --- | --- |
| **D1 Time Travel**: Cloudflare can put the database back to any minute in the past | 7 days | Cloudflare, automatically; nothing to set up | Almost every restore: it is quicker and simpler |
| **Encrypted export**: a copy of the whole database, locked with your backup key | 30 days | The GitHub workflow **Back up service data**, every day at 04:41 UTC | Anything older than 7 days, or if Time Travel cannot help |

The encrypted exports are stored in the R2 bucket `dbd-presence-production-backups` (and `dbd-presence-staging-backups`
for the test copy) as files named `d1/<date>T<time>Z.sql.gz.gpg`. The bucket deletes each file 30 days after it was made,
and its lock rule stops anyone deleting a file before it is 29 days old.
The service itself cannot open this bucket.

The pictures and files in the other storage buckets are not in these backups (they are not part of the database).

## 1. Make the backup key

Each copy gets its **own** key: one for staging and one for production. Do this twice.

1. Open **PowerShell** on your PC and paste this one line. It makes a random key and copies it to the clipboard
   without showing it:

   ```
   $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) | Set-Clipboard
   ```

2. **Save it first.** Paste it into your password manager as a new entry called `DBD Presence backup key (staging)` (or
   `(production)`). Also save it on the USB stick next to your release keys (`docs/owner/release-keys.md`), in a text
   file with the same name. **Without this key the backups cannot be read by anyone, including you.** There is no way
   to recover it.
3. Put it into GitHub (paste it straight from the clipboard; never into a chat, an email or a document):
   - **Staging**: open the repository on GitHub → **Settings** → **Environments** → **staging** → under **Environment
     secrets** click **Add environment secret** → name `BACKUP_KEY` → paste → **Add secret**.
   - **Production**: first do step 1a below, then add `BACKUP_KEY` the same way to the environment
     **production-backups**.
4. Clear the clipboard: in PowerShell paste `Set-Clipboard -Value ' '`. If you use clipboard history (Windows key + V),
   delete the entry there too.

Never send the backup key to anyone, including the developer. If it was ever shown to someone, make a new one (steps 1
to 4) and tell the developer; backups made with the old key can then only be read with the old key.

### 1a. Production only: a separate GitHub environment for backups

Production deploys need your approval (owner guide "Automatic setup from GitHub", step 2). A daily backup cannot wait
for an approval every day, so production backups run in their own environment, **production-backups**, which may only
run from `main` and holds a Cloudflare token that can reach the database and storage but cannot deploy anything.

1. **Settings** → **Environments** → **New environment** → `production-backups` → **Configure environment**. (If it is
   already listed, GitHub made it by itself on a scheduled run; open it and carry on.)
2. Under **Deployment branches and tags** choose **Selected branches and tags** → **Add deployment branch or tag
   rule** → `main` → **Add rule**. Do **not** tick **Required reviewers**.
3. Make the limited Cloudflare token: in the Cloudflare dashboard click your profile icon → **My Profile** → **API
   Tokens** → **Create Token** → **Custom token** → **Get started**. Name it `dbd-presence backups`. Add exactly two
   permissions and nothing else:
   - **Account** · **D1** · **Edit**: the backup reads and exports the database, and the restore drill creates and
     deletes its temporary database;
   - **Account** · **Workers R2 Storage** · **Edit**: the backup uploads the encrypted file, the drill downloads it, and
     the job checks the bucket's lock and expiry rules.

   Under **Account Resources** choose **Include** → your account. Click **Continue to summary** → **Create Token** and
   copy it. Do not add any Workers, Pages or Zone permission: this token must not be able to deploy anything.

   What this token can still do, plainly: both permissions cover the **whole** Cloudflare account, not just the
   production database and the backups bucket, and staging and production share that account. So this token, and the
   staging token, could also delete the live database or empty other buckets. The lock rule (step 2) keeps every backup
   for 29 days even then, as long as the rule is in place, and only `main` can use this environment (point 2). To close the gap completely, put staging
   in a second Cloudflare account (`docs/owner/cloudflare-setup.md`, "One Cloudflare account for both copies").
   Whether Cloudflare offers an R2 permission limited to one bucket for this kind of token, and whether the export
   works with a read-only D1 permission, has not been checked yet (PENDING-ENV); if the token page offers a
   bucket-limited R2 row, the developer will update this step.
4. On the `production-backups` page under **Environment secrets** add three secrets: `CLOUDFLARE_API_TOKEN` (the token
   from point 3), `CLOUDFLARE_ACCOUNT_ID` (the same account ID as in the other environments) and `BACKUP_KEY` (the
   production key from step 1).

Staging needs nothing extra: it uses the `staging` environment and its token, plus the `BACKUP_KEY` from step 1.
Recommended: limit the `staging` environment to `main` too (**Settings** → **Environments** → **staging** →
**Deployment branches and tags** → **Selected branches and tags** → `main`), so a workflow changed on another branch
cannot read the staging token or the staging backup key (owner guide "Automatic setup from GitHub", step 2). Changes
to `.github/` need your review once the `main` ruleset with code owners is on (same guide, step 5).

Until `BACKUP_KEY` exists for a copy, its daily backup run ends in red with **BACKUP_KEY is missing**. That is
expected and changes nothing; it stops once the key is there.

## 2. Make the backup storage

Run **Deploy service** once for each copy with **provision** ticked (owner guide "Automatic setup from GitHub", "Each
time: run it"). It creates the bucket `dbd-presence-<copy>-backups`, its lock rule `backups-29-days` and its expiry
rule `expire-after-30-days`. Its report lists all three.

- The **lock rule** means no backup can be deleted or overwritten until it is 29 days old, also not with a Cloudflare
  token, as long as the rule itself is in place. (Whether a token that may edit R2 can remove the rule first is not
  tested yet: PENDING-ENV.) It ends one day before the expiry, so old backups can still be deleted on time.
- The **expiry rule** deletes each backup 30 days after it was made.

If the report says a rule could not be set, add it by hand in **R2 object storage** → the `-backups` bucket →
**Settings**:
- **Bucket lock rules** → **Add rule** → name `backups-29-days`, all objects (no prefix), keep for 29 days;
- **Object lifecycle rules** → **Add rule** → name `expire-after-30-days`, all objects, delete after 30 days. Until
  this rule is set, the backup run deletes files older than 30 days itself and its report says so.

## 3. Run the restore drill once on staging

The drill proves that a backup can really be read and loaded. It loads the newest backup into a **new, temporary**
database, checks it and deletes it again. It never touches the live database's data.

1. On GitHub open **Actions** → **Back up service data** (listed once the workflow is on `main`) → **Run workflow**.
2. Keep **Use workflow from** on `main`, choose **staging**, tick **drill**, and click the green **Run workflow**.
3. Wait until both parts finish (about ten minutes). Open the run. On its **Summary** page the drill's report should
   say **The drill passed**, with a table of database tables and their row counts (no personal data: only table names
   and numbers).
4. Send the developer the Summary page of the drill (or copy it into `docs/evidence/restore-drill.md` yourself, as that
   file describes). This is the evidence for the launch check.

Do the same for **production** later, when the developer asks: choose **production**, tick **drill**.

If a drill is cancelled or times out before its last step, its temporary database is left behind with a copy of the
data. The daily backup run and every later drill delete such leftovers once they are a day old, and their report says
so under **Leftover drill databases**.

If the drill's report says the temporary database **could not be deleted**, delete it by hand: **Storage & databases**
→ **D1 SQL database** → the database whose name contains `restore-drill` → **Settings** → **Delete**. Never delete
`dbd-presence-production` or `dbd-presence-staging`.

## Every day after that

The backup runs by itself every day at 04:41 UTC for both copies; you do not need to do anything. GitHub emails you if
a run fails. Open the run and read its **Summary** page:

- **BACKUP_KEY is missing** (a run you started by hand): do step 1 for that copy. Until you have made the key, the
  daily run skips that copy without failing and shows a note saying so, so you get no daily failure emails.
- **R2 bucket … does not exist**: do step 2.
- **more than the 25.0 MB limit** or **would pass the 800.0 MB budget for production backups** (200.0 MB for
  staging): no backup was made, on purpose (see "Storage and the free tier" below). Time Travel still covers the last 7 days. Tell the developer.
- Anything else: tell the developer and send them the Summary page (it never contains the key or anyone's data).

## Storage and the free tier

Cloudflare's free tier holds 10 GB of R2 storage for the whole account, and staging and production are on the same
account, so everything below shares that one 10 GB (owner decision D18):

| What | Production | Staging |
| --- | --- | --- |
| The service's own files (packs, uploads, releases, audit exports) | at most 8 GB (warning and upload stop at 6 GB) | at most 750 MB (warning and upload stop at 500 MB) |
| Backups | at most 800 MB | at most 200 MB |

Worst case for the whole account: 8 + 0.75 + 0.8 + 0.2 = **9.75 GB**, below the 10 GB.

- A single backup may be at most **25 MB** (after compression and encryption), in either copy. 31 backups (30 days,
  plus the day the expiry rule may take) of 25 MB are 775 MB, inside production's 800 MB. Staging's 200 MB holds 31
  backups of up to about 6.4 MB; a staging backup that would pass its budget is refused like any other.
- Worst case without these limits: a D1 database can grow to 500 MB on the free plan. Its export as text is of a
  similar size and compresses to roughly 80 to 200 MB. Thirty of those would be up to about 6 GB per copy, which would
  break the free tier. That is why a too-large backup is refused instead of uploaded: the free-tier rule comes first.
- Today the database is tiny: an export of the empty service with its starting data is about 40 KB, and about 6 KB
  once compressed and encrypted, so 30 days of backups are well under 1 MB. The limit is reached only when the
  database text grows past roughly 120 MB (production) or 30 MB (staging); the developer then has to decide what to
  change.

The restore drill also makes a temporary database for a few minutes; the free plan allows 10 databases, and it is
deleted at the end of the drill.

## Restoring for real

This is rare. Tell the developer first, and write down the time the problem started (in UTC).

### Option A (recommended): D1 Time Travel, for the last 7 days

Time Travel puts the live database back to how it was at a moment you choose. Everything written after that moment
is lost, so choose a time just before the problem.

In the terminal from the owner guide step 8 (in the `service` folder, after `npx wrangler login`):

1. See what is possible: `npx wrangler d1 time-travel info dbd-presence-production`
2. Restore, with the time in UTC: `npx wrangler d1 time-travel restore dbd-presence-production --timestamp 2026-11-02T09:55:00Z`
3. Wrangler prints a **bookmark** for the state just before the restore. Save it: with
   `npx wrangler d1 time-travel restore dbd-presence-production --bookmark <bookmark>` you can undo the restore.

### Option B: from an encrypted export (older than 7 days)

This loads a backup into a **new** database and then points the service at it. The broken database is kept until you
are sure.

1. **Download the backup.** Cloudflare dashboard → **R2 object storage** → `dbd-presence-production-backups` → `d1/` →
   the newest file from before the problem → **Download**. It is called `<date>T<time>Z.sql.gz.gpg`.
2. **Unlock it.** You need gpg: it comes with **Git for Windows** (open **Git Bash**). In the folder with the file run
   `gpg --decrypt --output backup.sql.gz <file name>` and paste the production backup key when asked. It must say
   `encrypted with 1 passphrase` and must **not** say `WARNING: encrypted message has been manipulated`. Then run
   `gzip -d backup.sql.gz`, which gives `backup.sql`.
   `backup.sql` contains everyone's account data: keep it only on your own PC, never send it, and delete it (and the
   `.gz`) when the restore is done.
3. **Make a new database** with a name that ends in `-production`, for example with the date:
   `npx wrangler d1 create dbd-presence-r20261102-production --location weur`
4. **Load the backup into it:** `npx wrangler d1 execute dbd-presence-r20261102-production --remote --file backup.sql`
5. **Point the service at it.** Send the developer the new database's name. They change `database_name` for production
   in `service/wrangler.toml` to it; then you run **Deploy service** for production as usual. It applies any newer
   database tables and deploys the service with the restored data. The daily backups follow the new database by
   themselves.
6. When everything works for a week, delete the old database in the dashboard (**Storage & databases** → **D1 SQL
   database** → the old one → **Settings** → **Delete**), and delete `backup.sql` from your PC.

Never load a backup into the live database itself, and never copy production data into staging.
