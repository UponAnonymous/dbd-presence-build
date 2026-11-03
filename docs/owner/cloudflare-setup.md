# Setting up Cloudflare for arcavale.com

This guide is for the owner. It moves the domain's DNS to Cloudflare, protects the Owner Panel, creates the
database and file storage, and stores the secrets. It follows PLAN_V2 §1, §3 and §7 and ADR-0010, ADR-0011 and ADR-0014.

**Every step is owner-only**: it needs your accounts, your passwords or your second factor. The developer cannot do
them for you and must never be given a password, a secret or a recovery code. The only values you send to the developer
are the two database IDs from step 7 and the public keys from step 8. They are not secret.

Unauthenticated floods (many requests from many addresses) are stopped at Cloudflare's edge by the rule in step 11.
The service's own limits protect against individual clients and accounts; they are not meant to absorb floods.

Do everything twice where it says so: once for **staging** (the test copy) and once for **production** (the real
service). Keep them separate. Never copy production data or secrets into staging.

Rules for secrets:

- Never paste a secret into a chat, an email, a ticket, a document or the repository.
- Enter secrets only into Cloudflare itself, either through the commands below or in the Cloudflare dashboard.
- If a secret is ever shown to anyone else, replace it straight away (make a new one and enter it again).

## 1. Create a free Cloudflare account

1. Go to <https://dash.cloudflare.com/sign-up>.
2. Enter your email address and a long, unique password (use a password manager).
3. Open the confirmation email from Cloudflare and click the link.
4. Choose the **Free** plan whenever you are asked.

## 2. Add arcavale.com and copy the existing DNS records

1. In the Cloudflare dashboard click **Add a domain** (or **Add site**).
2. Type `arcavale.com`, choose **Quick scan for DNS records**, and click **Continue**.
3. Choose the **Free** plan and click **Continue**.
4. Cloudflare shows the records it found. Open Namecheap in another tab: **Domain List** → **Manage** next to
   arcavale.com → **Advanced DNS**.
5. Compare the two lists line by line. For every record in Namecheap that is missing in Cloudflare (often email
   records such as `MX` and `TXT`), click **Add record** in Cloudflare and copy the type, name, value and priority exactly.
6. Delete any record in Cloudflare that you do not recognise and that is not in Namecheap. A forgotten record that
   points somewhere you no longer control is a security risk.
7. Click **Continue**. Cloudflare now shows **two nameservers**, for example `ada.ns.cloudflare.com` and
   `bob.ns.cloudflare.com`. Keep this page open.

## 3. Point Namecheap at Cloudflare's nameservers

1. In Namecheap go to **Domain List** → **Manage** next to arcavale.com.
2. Find **Nameservers** and change the drop-down to **Custom DNS**.
3. Paste Cloudflare's first nameserver into the first box and the second into the second box.
   Remove any other entries.
4. Click the green tick to save.

The domain stays registered at Namecheap. Only the DNS moves.

## 4. Wait for activation

1. Back in Cloudflare, click **Check nameservers now**.
2. Wait. It usually takes under an hour and can take up to 24 hours.
3. Cloudflare emails you when arcavale.com is **Active**. Do not continue until it is.
4. Check that your email and anything else on the domain still works.

## 4a. Lock the domain at Namecheap and turn on DNSSEC

These stop someone from moving arcavale.com away from you or answering for it with fake DNS.

1. Turn on two-factor sign-in for Namecheap first: **Profile** → **Security** → **Two-Factor Authentication**, and
   choose an authenticator app (not SMS if you can avoid it). Save the backup codes in your password manager.
2. **Registrar lock**: **Domain List** → **Manage** next to arcavale.com → **Domain** tab → turn **Domain Lock** on.
   While it is on, nobody (including you) can transfer the domain away; switch it off only for a transfer you started.
3. **DNSSEC**, step one in Cloudflare: open **arcavale.com** → **DNS** → **Settings** → **DNSSEC** → **Enable DNSSEC**.
   Cloudflare shows a **DS record** (key tag, algorithm, digest type and digest). Keep the page open.
4. **DNSSEC**, step two in Namecheap: **Domain List** → **Manage** → **Advanced DNS** → **DNSSEC** → turn it on and
   **Add DS record**, then copy the key tag, algorithm, digest type and digest exactly from Cloudflare. Save.
5. Back in Cloudflare the DNSSEC page changes to **Success** once Namecheap has published the record (up to a day).

## 5. Turn on two-factor sign-in or a passkey for Cloudflare and GitHub

Cloudflare:

1. Click the person icon (top right) → **My Profile** → **Authentication**.
2. Add a **passkey** or a **security key** if you have one; otherwise turn on **Two-Factor Authentication** with an
   authenticator app.
3. Save the backup codes in your password manager. Do not keep them anywhere else online.

Namecheap: if you have not done it in step 4a, turn on two-factor sign-in now (**Profile** → **Security**).

GitHub:

1. Click your picture (top right) → **Settings** → **Password and authentication**.
2. Under **Passkeys** click **Add a passkey**, and turn on **Two-factor authentication** with an authenticator app.
3. Save the recovery codes in your password manager.

Your email account: whoever controls the email address you use for Cloudflare, GitHub and Namecheap can reset those
passwords, so it needs the same protection. Turn on a passkey or two-step sign-in there too, and save its recovery
codes in your password manager. (Security review 2026-10-25, TM-06: the email and domain accounts are part of what
keeps the service safe.)

## 6. Protect the Owner Panel with Zero Trust Access

Do this once for `admin.arcavale.com` (production) and once for `admin-staging.arcavale.com` (staging).

1. In the Cloudflare dashboard open **Zero Trust**. If asked, pick a team name (for example `arcavale`) and the
   **Free** plan. Your **team domain** is then `https://<team name>.cloudflareaccess.com`. Note it down.
2. Go to **Settings** → **Authentication** → **Login methods** → add **GitHub** as a login method (you sign in to
   GitHub with the passkey from step 5). This is the recommended way in. **One-time PIN** (a code sent by email) also
   works, but then anyone who gets into that email account gets into the panel. Once GitHub sign-in works (step 7),
   remove One-time PIN from this application (the application's **Login methods** tab: untick it).
3. Go to **Access** (newer dashboards: **Access controls**) → **Applications** → **Add an application** → **Self-hosted**
   (newer dashboards ask for the kind of destination: choose **Public DNS**).
4. Name it `Owner Panel` (or `Owner Panel staging`). Under the domain, enter `admin.arcavale.com` (or
   `admin-staging.arcavale.com`) and leave the path empty, so the whole site is protected. Check that the
   **Subdomain** box says `admin` (or `admin-staging`): left empty, the application protects `arcavale.com` instead.
5. Add a policy: name `Owner`, action **Allow**, rule **Include** → **Emails** → your email address. Add nothing else.
   Use an address that is **not** the public contact address in the privacy notice, so nobody can guess which account
   opens the panel. Set the application's **Session duration** to **8 hours** or less.
6. Save the application. Open it again and copy the **Application Audience (AUD) Tag**. You need it in step 8.
7. Test it: open `https://admin.arcavale.com` in a private browser window. Cloudflare must ask for your email and a
   code (or your GitHub sign-in) before anything else loads.

## 7. Create the database and file storage

The names must be exactly as below; they are the names in `service/wrangler.toml`.

1. In the Cloudflare dashboard go to **Storage & databases** → **D1 SQL database** → **Create database**.
   - Create `dbd-presence-staging`.
   - Create `dbd-presence-production`.
   - Choose a location in **Western Europe** if asked (you are in the UK; this keeps the data close to you).
2. Open each database and copy its **Database ID**. Send both IDs to the developer; they are not secret and go into
   `wrangler.toml`.
3. Go to **R2 object storage** → **Create bucket** and create these six buckets, each with the default settings:
   - `dbd-presence-staging-packs`
   - `dbd-presence-staging-uploads`
   - `dbd-presence-staging-audit`
   - `dbd-presence-production-packs`
   - `dbd-presence-production-uploads`
   - `dbd-presence-production-audit`
4. The two `-audit` buckets receive the daily audit export. Lock them so nothing in them can be changed or deleted,
   for exactly the audit retention and no longer, and let old exports expire after it. In the terminal from step 8
   (after `npx wrangler login`):

   ```
   npx wrangler r2 bucket lock add dbd-presence-production-audit --name audit-90-days --retention-days 90
   npx wrangler r2 bucket lifecycle add dbd-presence-production-audit --name expire-after-90-days --expire-days 90
   ```

   Do the same for `dbd-presence-staging-audit`. In the dashboard the same settings are under the bucket →
   **Settings** → **Bucket lock rules** and **Object lifecycle rules**. Never make the lock longer than 90 days: the
   exports must not outlive the audit retention.

## 8. Enter the secrets

You need [Node.js](https://nodejs.org/) 22 and a copy of the repository on your PC. In a terminal:

```
cd service
npm ci
npx wrangler login
```

`wrangler login` opens your browser to sign in to Cloudflare; approve it there.

First enter the three Access values. Each command asks you to type or paste the value; it is not shown on screen or
saved. The first time, wrangler may say the Worker does not exist yet and offer to create it: answer **yes**.

```
npx wrangler secret put ACCESS_AUD          --env production   # the AUD tag from step 6
npx wrangler secret put ACCESS_TEAM_DOMAIN  --env production   # https://<team name>.cloudflareaccess.com
npx wrangler secret put OWNER_EMAILS        --env production   # your email; several are separated by commas
```

Then generate the keys. **Back up every key offline before storing it**: Cloudflare never shows a secret again, and
the backup is the only copy you can read. Put a removable drive you keep offline (or an encrypted folder you copy to
one) in the PC; below it is `E:\dbdp-keys`. Each command writes the key to its backup file (`--out`, never over an
existing file) and pipes it straight into Cloudflare, so it never appears on screen:

```
node scripts/new-secret.mjs invite-keys invite-prod-1  --out E:\dbdp-keys\invite-prod-1.json  | npx wrangler secret put INVITE_HMAC_KEYS    --env production
node scripts/new-secret.mjs ban-key     ban-prod-1     --out E:\dbdp-keys\ban-prod-1.txt      | npx wrangler secret put BAN_HMAC_KEY        --env production
node scripts/new-secret.mjs signing-key licence-prod-1 --out E:\dbdp-keys\licence-prod-1.json | npx wrangler secret put LICENCE_SIGNING_KEY --env production
node scripts/new-secret.mjs signing-key config-prod-1  --out E:\dbdp-keys\config-prod-1.json  | npx wrangler secret put CONFIG_SIGNING_KEY  --env production
node scripts/new-secret.mjs signing-key pack-index-prod-1 --out E:\dbdp-keys\pack-index-prod-1.json | npx wrangler secret put CONTENT_SIGNING_KEY --env production
```

Then unplug the drive and keep it somewhere safe, away from the PC.

`BAN_HMAC_KEY` keeps the ban list. **Never rotate it** and keep its backup: if it were replaced, every ban you have
made would quietly stop working.

`CONTENT_SIGNING_KEY` signs the list of image packs (the pack index) each time you publish or withdraw something in the
Owner Panel, and the character list (the roster) each time a character is added, renamed or removed and once a week.
Its name must start with `pack-index-`, because the app looks for exactly that name.

Each signing-key command also prints a **public key** on the screen, already written as the line the app needs, and
says which list it belongs in. Public keys are not secret: copy each line and either send them to the developer, who
builds them into the app, or add them yourself as described below.

### Rotating the invite key later

`INVITE_HMAC_KEYS` is a key ring: `{"current": "<name>", "keys": {"<old name>": "<key>", "<new name>": "<key>"}}`.
To rotate it, add a new key and keep the old ones, so codes made under them still work until they expire. **Never put
a single new key in its place** (that would invalidate every unused code). Build the merged ring from your backup of
the current one; the command checks it, writes the new backup and stores it:

```
node scripts/new-secret.mjs rotate-invite-keys invite-prod-2 --from E:\dbdp-keys\invite-prod-1.json --out E:\dbdp-keys\invite-prod-2.json | npx wrangler secret put INVITE_HMAC_KEYS --env production
```

Remove an old key only after its last code has expired (90 days at most): run the same kind of merge by hand from the
latest backup, or ask the developer.

**Putting the public keys into the app (production keys).** The app trusts only public keys compiled into it, in
`src/DbdPresence.Online/TrustedServiceKeys.json`. It has two separate lists, `staging` and `production`:

- `production` starts **empty** on purpose. Paste each line printed above (for `licence-prod-1`, `config-prod-1` and
  `pack-index-prod-1`) into the `production` list, separated by commas. Each line names the purposes that key may
  sign (`licence`, `config`, or for the content key `pack-index` and `roster`); paste the lines as printed and never
  add a purpose by hand.
- A **release build refuses to compile** while `production` is empty, or if it contains any key that is also in `staging`
  (`python3 build/check-online-keys.py --release`, run by the release build and listed in `docs/RELEASE_CHECKLIST.md`).
  Ordinary development and CI builds use the `staging` list and the staging host, so they keep working meanwhile.
- The `staging` entries that ship in the repository are placeholders. `licence-staging-1` and `config-staging-1` are
  real, valid keys, but nobody holds their private halves. `pack-index-staging-1` has no key at all yet: it is marked
  `"awaitingKey": true`. When you run the staging block below, replace each of the three entries with the line printed
  there (same names; for `pack-index-staging-1` this also removes `"awaitingKey": true`), or the app will not accept
  staging licences, config or packs.

Now repeat both blocks for staging: use `--env staging`, and `-staging-` instead of `-prod-` in each name. For staging
only, also enter the staging Discord application's ID (a public number, from the Developer Portal's
**General Information** page):

```
npx wrangler secret put DISCORD_CLIENT_ID --env staging
```

You can also type the Access values and the Discord secret in the dashboard instead: **Workers & Pages** → the Worker →
**Settings** → **Variables and Secrets** → **Add** → type **Secret**. The keys must come from the commands above.

## 8a. Optional: the key for automatic labelling (D37)

Only needed for the Study page of your own app (automatic labelling of your study recordings, `docs/PLAN_AUTO_LABELLING.md`).
Without it, everything else works and the Study page says that automatic labelling is not set up.

1. Create an API key in the model provider's console (the account you pay from). While you are there, set a **monthly
   spend limit** you are happy with: about $2 per hour of play is the estimate, the real cost is shown on the Study page.
2. Store it on the service. **On the Cloudflare website** (nothing to install): dash.cloudflare.com → **Workers & Pages**
   → `dbd-presence-service-production` → **Settings** → **Variables and Secrets** → **Add** → Type **Secret**, Name
   `STUDY_LABELLER_KEY`, Value: paste the key → **Deploy**. Or, if you already use the terminal from step 8, the command
   below asks you to paste it; either way it is not shown or saved anywhere else:

```
npx wrangler secret put STUDY_LABELLER_KEY --env production
```

The provider's address and the two header names it needs are the Worker variable `STUDY_LABELLER_PROVIDER` in
`service/wrangler.toml` (one JSON value for staging and one for production; the model is `STUDY_LABELLER_MODEL`). They are
kept there and not in the program's source so that the release branding scan of the Worker bundle stays on. If the
variable is missing or invalid (not an `https` address, a header name with spaces), the relay answers "not set up" and
nothing is sent. The pictures show other players' names and go to this provider: read its terms for how long it keeps
them and whether it trains on them before you create the key, and tell the app's Study page nothing is stored on the
service. The provider is **awaiting your approval** (`docs/evidence/privacy-approval.md`, entry for automatic labelling).

Only accounts you marked as your own in the Owner Panel (**Characters → "Mark as my game account"**, the same switch
that lets your app report new characters) can use it, and at most 300 requests a day each. To stop automatic labelling at any time, delete the key:
`npx wrangler secret delete STUDY_LABELLER_KEY --env production`.

## 9. Enter the Discord client secret

Do this for the production application (**Dead by Daylight**) and for your staging application. The production
application is your own Discord application **Dead by Daylight**, application ID `1563291325107937340` (already in
`wrangler.toml`): check that this number is the **Application ID** on its **General Information** page before you
copy its secret. If you have no
staging application yet, click **New Application** on the page below, name it `DBD Presence staging`, and use it only
for staging.

1. Open <https://discord.com/developers/applications> and click the application.
2. Go to **OAuth2**. Under **Redirects**, check that `http://127.0.0.1/callback` is listed (add it and save if not).
3. Under **Client Secret** click **Reset Secret**, confirm, and click **Copy**.
4. In the terminal from step 8 run the command below and paste the secret when asked:

   ```
   npx wrangler secret put DISCORD_CLIENT_SECRET --env production
   ```

   (For staging use `--env staging` with the staging application's secret.)
5. Do not paste the secret anywhere else. If you copied it somewhere by mistake, reset it again and repeat step 4.

## 9a. The content host (cdn.arcavale.com)

Pack images are served from `cdn.arcavale.com` (and `cdn-staging.arcavale.com` for staging) by the same Worker. You do
not create these DNS records yourself: the first deployment adds them as **Custom Domains** of the Worker
(`service/wrangler.toml`). Afterwards, check in **Workers & Pages** → the Worker → **Settings** → **Domains & Routes**
that `cdn.arcavale.com` is listed next to `api.arcavale.com`.

The images are public on purpose: Discord loads them by their address. Only addresses of the form
`/packs/<64 letters and digits>.png` or `.jpg` return anything; every other address answers "Not found". How to take
images down again is in `docs/owner/takedown-runbook.md`.

## 10. Always use HTTPS

1. In the Cloudflare dashboard open **arcavale.com** → **SSL/TLS** → **Edge Certificates**.
2. Turn on **Always Use HTTPS**. (The service also refuses plain `http://` itself; this makes browsers and the app
   never try it.)

## 11. Add the rate-limit rule

This stops floods at Cloudflare's edge before they reach the service (NFR-SVC-03). It is the layer for
**unauthenticated floods**, from one address or many; the service's own limits are a second line per client and
per account. It covers the whole API host, so `GET /v1/config` and every other route are protected as well.

1. In the Cloudflare dashboard open **arcavale.com** → **Security** → **WAF** → **Rate limiting rules** →
   **Create rule**.
2. Rule name: `API flood`.
3. Click **Edit expression** and paste (every method and every path on the two API hosts):

   ```
   (http.host in {"api.arcavale.com" "api-staging.arcavale.com"})
   ```

4. **With the same characteristics**: **IP**.
5. **When rate exceeds**: `10` requests per `10 seconds`.
6. **Then take action**: **Block**, for `10 seconds` (or longer if your plan offers it).
7. Click **Deploy**.

The free plan allowed one rate-limiting rule with a 10-second period when this guide was written; check what the
dashboard offers. If you later have more rules, add a second one for `admin.arcavale.com` with path starting `/api/`,
`30` requests per `10 seconds`, and a stricter one for `POST` to `/v1/auth/discord`, `/v1/auth/refresh` and
`/v1/invites/redeem` at `5` per `10 seconds`.

## 12. Publish the Owner Panel on Cloudflare Pages

The panel is a static site (`panel/` in the repository). It goes on two Pages projects, one per environment, behind
the Access applications from step 6. Do staging first. Settings are in `panel/wrangler.toml`; no account ID is stored
there.

1. Build the staging site on your PC: in `panel/` run `npm ci` and `npm run build -- --env staging`. (The staging
   build allows image previews from `cdn-staging.arcavale.com`; the production build from `cdn.arcavale.com`.) In
   `service/` run `npm ci` (it provides `wrangler`). Sign in once with `npx wrangler login` from `service/`.
2. From `panel/`, create and fill the staging project with one command (Wrangler asks to create the project the first
   time; answer yes and keep the production branch `main`):

   ```
   ../service/node_modules/.bin/wrangler pages deploy dist --project-name dbd-presence-panel-staging --branch main
   ```

3. In the Cloudflare dashboard open **Workers & Pages** → **dbd-presence-panel-staging** → **Custom domains** →
   **Set up a custom domain**, enter `admin-staging.arcavale.com` and confirm. The Access application from step 6
   already covers this host, so nothing is reachable without signing in.
4. Close the other ways in (ADR-0014: no bypass routes):
   - **Settings** → **General** → **Access policy** (newer dashboards: **Preview access** → **Restrict previews**):
     enable it. This protects the preview addresses
     (`*.dbd-presence-panel-staging.pages.dev`).
   - **Zero Trust** → **Access** → **Applications**: open `Owner Panel staging` and add a second domain,
     `dbd-presence-panel-staging.pages.dev`, so the project's own `pages.dev` address asks for sign-in too.
   - Always deploy with `--branch main` (as above), so no preview deployment is created.
5. Check it: open `https://admin-staging.arcavale.com` in a private window. Cloudflare asks you to sign in, then the
   panel shows the amber **Staging** banner. Open the **Users** section: it must show the list (or "No users yet"),
   not "Unexpected response". That proves the Worker route `admin-staging.arcavale.com/api/*` from the service answers
   the panel's API calls. Until the developer has deployed the service, the section shows an error instead; repeat
   this check after the first deployment. Also open `https://dbd-presence-panel-staging.pages.dev`: it must ask you
   to sign in.
6. Repeat steps 2 to 5 for production with the project name `dbd-presence-panel`, the domain `admin.arcavale.com`
   and the Access application `Owner Panel`, after building with plain `npm run build` (no `--env`). Production shows
   a **Production** badge and no banner. If pack previews in the production panel show only their file names, the
   staging build was deployed by mistake: build again without `--env` and deploy again.

The panel's security headers, including the Content-Security-Policy from ADR-0014, come from the `_headers` file in
`dist/`. To check them, open the browser's developer tools on the panel, **Network** tab, reload, select the page and
look for `Content-Security-Policy` under the response headers.

## When you are done

Tell the developer that steps 1 to 12 are finished, and send only the two database IDs and the public keys (or add the
public keys to `TrustedServiceKeys.json` yourself, as described in step 8). Keep the key backups offline; never send
them. The
developer then adds the IDs to `wrangler.toml`, applies the database tables and deploys staging first.

## Automatic setup from GitHub

Instead of steps 7 and 8 (and the deploy commands in step 12) you can let GitHub do the work. The workflow
**Deploy service** checks the code, creates the database, the storage buckets with the audit lock and expiry from step
7.4, the keys and the Owner Panel's Pages project, applies the database tables, deploys the service and the panel,
and then checks the live addresses. Running it again is safe: it only creates what is missing and never replaces a key
that exists. You do not need to send the developer any database IDs. The manual steps above still work and are the
alternative if you prefer them.

You still do steps 1 to 6, 10 and 11 yourself, and you enter the values only you have (step 9's Discord secret and
the Access values from step 6) in the dashboard, as described below.

**One difference to know about.** Keys made by the workflow exist only inside Cloudflare: there is no offline backup.
That is fine for normal running, but `BAN_HMAC_KEY` cannot be restored if the Worker is ever deleted, and the invite
key ring cannot be rotated by merging (the workflow refuses to rotate it, and it never rotates `BAN_HMAC_KEY`). If you
want offline backups of every key, use step 8 by hand instead, before the first run.

### Once: prepare GitHub

1. Create the two environments: **Settings** → **Environments** → **New environment**. Create `staging`, then
   `production`.
2. On the `production` page:
   - tick **Required reviewers**, add yourself, and click **Save protection rules**. Leave **Prevent self-review**
     unticked, because you are the reviewer;
   - under **Deployment branches and tags** choose **Selected branches and tags** → **Add deployment branch or tag
     rule** → `main` → **Add rule**.

   On the `staging` page, recommended: do the same **Deployment branches and tags** rule (`main` only). Then nothing
   on another branch can read the staging token (see "One Cloudflare account for both copies" below for why that
   matters). The cost: the developer must merge a change into `main` before it can be tried on staging. If you would
   rather keep staging open to other branches, tick **Required reviewers** on it instead, so each staging run waits for
   you.
3. On **each** environment's page, under **Environment secrets**, click **Add environment secret** twice:
   `CLOUDFLARE_API_TOKEN` (the token, made as in "The Cloudflare tokens" below) and `CLOUDFLARE_ACCOUNT_ID`. The
   credentials live in the environments, not as repository secrets, so a production run can read them only after
   passing the rules in step 2.
4. Delete any repository-level copies: **Settings** → **Secrets and variables** → **Actions** → **Repository
   secrets** → delete `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` if they are listed there.

   Do this before the first production run: GitHub creates a missing environment by itself, without any protection.
5. Protect the `main` branch, so a workflow file can only change through a reviewed pull request. **Settings** →
   **Rules** → **Rulesets** → **New ruleset** → **New branch ruleset**. Name it `main`, set **Enforcement status** to
   **Active**, and under **Target branches** add **Include default branch**. Tick **Restrict deletions**, **Block force
   pushes** and **Require a pull request before merging**; inside that, set **Required approvals** to `1` and tick
   **Require review from Code Owners**. Click **Create**.
   - The file `.github/CODEOWNERS` names you as the owner of `.github/` (the workflows), `service/scripts/` (what the
     workflows run with your Cloudflare token), `build/` and the release signer. With the rule above, a change to any of
     them reaches `main` only after **you** approved it.
   - GitHub does not let anyone approve their own pull request. If you make pull requests yourself, add yourself under
     **Bypass list** (Repository admin) so you can still merge them; the developer's pull requests then wait for you.

### The Cloudflare tokens (what each one may do)

Make each token in the Cloudflare dashboard: your profile icon → **My Profile** → **API Tokens** → **Create Token** →
**Custom token** → **Get started**. Give it a name, add the permissions below one row at a time, under **Account
Resources** choose **Include** → your account (and under **Zone Resources**, where a row is a zone permission,
**Include** → **Specific zone** → `arcavale.com`), then **Continue to summary** → **Create Token**. Copy it straight
into the GitHub environment secret; it is shown only once. Cloudflare's dashboard sometimes renames these rows; where a
name below is marked *(check the name on the token page)*, pick the row that matches the description.

| Token | Where it goes | Permissions |
| --- | --- | --- |
| `dbd-presence deploy staging` | GitHub environment `staging` | **Account** · **D1** · **Edit** (database and tables); **Account** · **Workers R2 Storage** · **Edit** (buckets, lock and expiry rules, backups); Account · Workers Scripts · Edit (upload the Worker and its secrets) *(check the name on the token page)*; Account · Cloudflare Pages · Edit (the Owner Panel) *(check the name on the token page)*; Zone · Workers Routes · Edit for `arcavale.com` (the `api`, `admin` and `cdn` addresses) *(check the name on the token page)* |
| `dbd-presence deploy production` | GitHub environment `production` | The same as staging. Make it a **separate** token, so you can revoke one without the other. |
| `dbd-presence backups` | GitHub environment `production-backups` | **Account** · **D1** · **Edit** and **Account** · **Workers R2 Storage** · **Edit** only (`docs/owner/backups.md`, step 1a). It cannot deploy anything. |

Staging backups use the `staging` deploy token. If the first deploy run says a permission is missing, the error
names the API call; add the matching row to that token (**My Profile** → **API Tokens** → the token → **Edit**) and run
it again. Never widen a token to **All accounts** or **All zones**.

### One Cloudflare account for both copies: what it means

Today staging and production live in **one** Cloudflare account. Cloudflare cannot limit most of the permissions
above to one database, one Worker or one bucket: a D1, R2 or Workers permission covers the **whole account**. So the
**staging** token can also change or delete **production's** database, Worker and buckets. Anyone who could read the
staging token (for example through a changed workflow on a branch) could damage production.

Three things limit this: the `main`-only rule on `staging` (step 2 above), the code-owner review of `.github/` (step 5),
and the lock rules on the audit and backup buckets (nothing in them can be deleted before it is 90 or 29 days old).
The lock rules are a brake, not a wall: a token that may edit R2 may also be able to remove a lock rule first. Whether
Cloudflare allows that has not been tested yet (PENDING-ENV). D1 Time Travel cannot be switched off, but a token that
can delete the database deletes its history with it.

**Recommended: a second Cloudflare account just for staging.** It is free. Sign up for a new Cloudflare account with
another email address (step 1), give it two-step sign-in (step 5), and use it only for the staging copy: its D1
database, buckets, Worker and Pages project, with the `staging` token made in that account. Then the staging token
cannot reach production at all. The `arcavale.com` zone stays in your main account; the developer then has to move the
staging addresses (`api-staging`, `admin-staging`, `cdn-staging`) to a domain or zone in the staging account, so ask
them before you switch.

### Each time: run it

1. Open the repository on GitHub and click the **Actions** tab.
2. In the list on the left click **Deploy service**. (It is listed once the workflow is on the `main` branch.)
3. Click **Run workflow** on the right. Keep **Use workflow from** on `main`, choose **staging**, keep **provision**
   ticked and leave **rotate** unticked. Click the green **Run workflow** button.
4. Wait until the run finishes (about ten minutes), open it and read the report on its **Summary** page. It says in
   plain words what was created, what is live and what you need to do next.
5. The **first** run ends in red with **Not deployed yet**. That is expected: the service waits for the values only
   you can enter. In the Cloudflare dashboard open **Workers & Pages** → **dbd-presence-service-staging** →
   **Settings** → **Variables and Secrets** → **Add**. For each value choose type **Secret**, type the name exactly as
   shown, paste the value and click **Deploy**:
   - `ACCESS_AUD`: the Application Audience (AUD) Tag from step 6;
   - `ACCESS_TEAM_DOMAIN`: `https://<team name>.cloudflareaccess.com` from step 6;
   - `OWNER_EMAILS`: your email address (several are separated by commas);
   - `DISCORD_CLIENT_SECRET`: do step 9 points 1 to 3 for the staging application, then paste the secret here instead
     of into a terminal;
   - `DISCORD_CLIENT_ID` (staging only): the staging application's **Application ID** from the Discord Developer
     Portal's **General Information** page.
6. Run the workflow again with the same choices. The report now says **The service is live**.
7. Attach the panel's address and close the other ways in: step 12, points 3 to 5. The report reminds you of them.
8. Send the developer the public keys: they are in the report and in the file **public-keys-staging** at the bottom of
   the run's Summary page. They are not secret.
9. When staging works, do the same with **production** and the Worker **dbd-presence-service-production**. GitHub
   asks you to approve the run: click **Review deployments**, tick **production** and click **Approve and deploy**.
   Production needs no `DISCORD_CLIENT_ID`; it is already in `wrangler.toml`.

Leave **rotate** unticked unless the developer asks you to replace the signing keys. Copies of the app built with the
old public keys stop accepting licences and configuration until a build with the new keys is released.

If the report says that **appeal.arcavale.com** changed, stop and do not run the workflow again until it is fixed:
the workflow never edits DNS records. In the dashboard open **arcavale.com** → **DNS** → **Records** and check that
`appeal` is an **A** record with the value `207.244.229.132` and the proxy status **DNS only** (grey cloud).

If the report says a lock or expiry rule could not be set, add it in the dashboard as in step 7.4: **R2 object
storage** → the `-audit` bucket → **Settings** → **Bucket lock rules** (90 days, all objects) and **Object lifecycle
rules** (delete after 90 days).

The workflow also creates the bucket for the daily encrypted database backups, `dbd-presence-<copy>-backups`, with a
29-day lock rule `backups-29-days` (no backup can be deleted or overwritten before it is 29 days old) and a 30-day
expiry rule. The backups themselves, their key and the restore drill are set up in `docs/owner/backups.md`.
