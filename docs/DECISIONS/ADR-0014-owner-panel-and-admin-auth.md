# ADR-0014: Owner Panel and admin authentication

- Status: **Accepted** (2026-10-23)
- Source: PLAN_V2 §7, §9, §10
- Related: ADR-0010 (hosting), ADR-0011 (identity), ADR-0015 (uploads)

## Context

The panel manages invites, users, uploads, packs, Discord applications, access mode, releases and the audit log. It displays user-controlled text and holds the highest-privilege path into the service, so XSS or an admin bypass is rated low-likelihood and critical-impact.

## Decision

- **Layout**: a separate static web app. Its admin API is on the same origin, `https://dbdadmin.arcavale.com/api/*`. The user app contains no panel code.
- **Authentication**: Cloudflare Access (passkey or authenticator app) in front of `dbdadmin.arcavale.com`. The Worker verifies `Cf-Access-Jwt-Assertion` on every admin call: signature against the team JWKS, `aud`, `iss`, `exp`, and an owner email allow-list.
- **No bypass routes**: the `workers.dev` route and Pages preview domains are switched off. A staging test asserts that a direct unauthenticated call returns 401.
- **Request protection** on every state-changing call:
  - `Origin` must equal exactly `https://dbdadmin.arcavale.com`;
  - `Sec-Fetch-Site` values other than `same-origin` are rejected;
  - a custom header is required, so a cross-origin call fails its preflight;
  - no CORS.
- **Content Security Policy** (recorded verbatim so it is not loosened ad hoc):

  ```
  default-src 'self'; img-src 'self' https://cdn.<domain>; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'
  ```

  `<domain>` is `arcavale.com` in production. There is no inline script.
  - *Staging* (amended 2026-10-24, V2-S4a): the staging panel is built with `node scripts/build.mjs --env staging`, which
    changes only the content host in `img-src`, to `https://cdn-staging.arcavale.com`, so that each panel previews pack
    images from its own environment's content host and from no other. Nothing else in the policy differs; a test checks
    the build output of both. This is a substitution, not a loosening.
  - *Pack image previews* (V2-S4a): the panel loads an `<img>` only when its address is exactly
    `https://cdn[-staging].arcavale.com/packs/<64 hex>.(png|jpg)` for its own environment (`panel/src/cdn.ts`), with
    `referrerpolicy="no-referrer"`. Before an upload the panel shows the chosen file's name and size only: the policy
    allows no `blob:` or `data:` images, and it is not loosened for a pre-upload preview.
- **Rendering**: user values are rendered only as text, never `innerHTML` (lint rule and test). Pasted image URLs are shown as inert text and never loaded, because loading them would reveal the owner's IP.
- **Audit log**: every privileged action records who, when, what, and before/after. Entries are hash-chained. A daily append-only export goes to a separate R2 bucket with a bucket lock (retention rule) and a write-only binding. Destructive actions need confirmation.
  - *Export contents* (amended 2026-10-24, `service/src/audit-export.ts`): per row only seq, time, actor, action, target, the salted digests of the before/after payloads, prev_hash and hash, plus the chain head at export time and the previous export's head. No payload is exported. Each row has a 16-byte random salt kept only in D1 and folded into both digests and the row hash, so nothing can be brute-forced back from an export.
  - *What an export proves*: on its own, that the exported rows form an unbroken chain from the previous export's head; together with D1, that every exported row still exists with the same metadata, hash and payload digests (not changed, not removed). Truncation of the newest rows is caught because the retention job never deletes the last exported head row and `GET /api/audit/verify` reports `exportedHead.present`. It does not prove anything about payload content without D1, by design.
  - *Retention*: the bucket lock retention is 90 days, no longer than the audit retention in D1, and exports older than 90 days are deleted by a lifecycle rule (owner guide).
  - *Schema history*: the service has never been deployed, so the salt, the export state and the ban table were folded into `0001_initial.sql` (2026-10-24); there is one hash format. Migrations are forward-only from the first deployment on.
  - *Owner allow-list*: emails are compared ASCII-only, case-folded for A–Z only; a non-ASCII entry makes the admin API answer 503. A failed fetch of the Access key set is remembered for 30 seconds.
- **Environments**: `staging` and `production` are fully separate (data, keys, Access apps, Discord OAuth app, HMAC key). The panel shows a staging banner. Production data is never copied into staging.
- **Sections**: Dashboard, Invites, Users, Uploads, Packs, Discord applications, Access and features, Releases, Audit log, Settings. The first release (V2-S3) ships Invites, Users and Audit.
- **Design**: the desktop app's tokens and typography; responsive and keyboard accessible.
- **Cookie flags**: the Access cookie on `admin.` is checked on staging for `HttpOnly`, `Secure` and the strictest working `SameSite`. Cloudflare's defaults are **UNKNOWN** until checked.

## Consequences

- The audit log is **tamper-evident**, not tamper-proof: someone with full control of the Cloudflare account could still alter it.
- Cloudflare Access free for 50 seats is **SECONDARY** (ADR-0010).
- The panel's build output is scanned by `build/check-release-branding.py` in CI, together with the Worker bundle and a staging API response and its headers.
- The panel only uploads release feeds and packages signed elsewhere; it never holds the release key (ADR-0012).
- TypeScript strict mode, schema validation, Vitest and Miniflare tests, exact dependency versions with a lock file and `npm audit` in CI, and the 14-day minimum package age apply.

## Alternatives

- **Separate origin for the admin API with CORS**: rejected; the plan uses one origin and no CORS.
- **Own login for the panel**: not chosen; Cloudflare Access is in front of every request.
- **Inline scripts**: rejected; `script-src 'self'` with no inline script.
- **Loading pasted image URLs in the panel for preview**: rejected (IP disclosure).
