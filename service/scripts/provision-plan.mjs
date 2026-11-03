// Decisions for the deploy workflow (.github/workflows/deploy-service.yml, scripts/provision.mjs). No Node APIs, so the
// Vitest suite (which runs in the Workers runtime) can check every rule: which secrets the job may generate, which it
// must never replace, what the owner still has to enter, how the per-deploy wrangler config is made, and how the
// result is explained to the owner. scripts/provision.mjs does the I/O (Cloudflare API, wrangler, files).
//
// FR-ADM-05 (audit export lock), FR-ADM-06 (separate environments), FR-ADM-02 / gate V3.4 (no workers.dev, no
// unauthenticated admin API), NFR-SVC-04 (signed config document), NFR-SVC-05 (keys are Worker secrets, staging has
// its own set).

import { purposeForKid, purposesFor } from "./trusted-key-entry.mjs";

export const ENVIRONMENTS = ["staging", "production"];

/** The short environment name used in key ids, as in docs/owner/cloudflare-setup.md step 8 (licence-prod-1). */
export const KID_ENV = { staging: "staging", production: "prod" };

/** Pages projects of the Owner Panel (panel/wrangler.toml). */
export const PAGES_PROJECTS = { staging: "dbd-presence-panel-staging", production: "dbd-presence-panel" };

export const HOSTS = {
  staging: { api: "api-staging.arcavale.com", admin: "admin-staging.arcavale.com" },
  production: { api: "api.arcavale.com", admin: "admin.arcavale.com" },
};

/** The DNS record that belongs to another service and must never change (A, DNS only). */
export const PROTECTED_RECORD = { name: "appeal.arcavale.com", type: "A", value: "207.244.229.132" };

/** D1 location hint: the owner is in the UK (owner guide step 7). */
export const D1_LOCATION = "weur";

/** The audit export bucket's binding (wrangler.toml) and its 90-day lock and expiry (owner guide step 7.4). */
export const AUDIT_BINDING = "AUDIT_EXPORT";
export const AUDIT_DAYS = 90;
export const AUDIT_LOCK_RULE_ID = "audit-90-days";
export const AUDIT_LIFECYCLE_RULE_ID = "expire-after-90-days";
const DAY_SECONDS = 86400;

const KID = /^[A-Za-z0-9._-]{1,32}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Secrets the job generates with scripts/new-secret.mjs. `purpose` is the TrustedServiceKeys.json purpose of a
 * signing key's public half. `rotation` says whether `rotate` may replace an existing value, and if not, why.
 */
export const GENERATED_SECRETS = {
  INVITE_HMAC_KEYS: {
    kind: "invite-keys",
    kidPrefix: "invite",
    purpose: null,
    rotation:
      "not rotated by the workflow: a rotation must merge the new key into the current ring so that unused invite codes keep working, and the workflow cannot read the current ring back from Cloudflare. Rotate it by hand (owner guide, \"Rotating the invite key later\"); that needs an offline backup of the current ring, which exists only if the ring was made by hand.",
  },
  BAN_HMAC_KEY: {
    kind: "ban-key",
    kidPrefix: "ban",
    purpose: null,
    rotation: "never rotated: replacing it would quietly lift every recorded ban.",
  },
  LICENCE_SIGNING_KEY: { kind: "signing-key", kidPrefix: "licence", purpose: "licence", rotation: null },
  CONFIG_SIGNING_KEY: { kind: "signing-key", kidPrefix: "config", purpose: "config", rotation: null },
  CONTENT_SIGNING_KEY: { kind: "signing-key", kidPrefix: "pack-index", purpose: "pack-index", rotation: null },
};

/** Secrets only the owner can provide, with where the value comes from (owner guide steps 6 and 9). */
export const OWNER_SECRETS = {
  DISCORD_CLIENT_ID: "the staging Discord application's Application ID (Discord Developer Portal → your staging application → General Information). It is a public number.",
  DISCORD_CLIENT_SECRET: "the Discord application's client secret (Discord Developer Portal → the application → OAuth2 → Client Secret → Reset Secret → Copy; owner guide step 9).",
  ACCESS_AUD: "the Application Audience (AUD) Tag of the Owner Panel's Access application (Zero Trust → Access → Applications → the application; owner guide step 6).",
  ACCESS_TEAM_DOMAIN: "your Zero Trust team domain, https://<team name>.cloudflareaccess.com (owner guide step 6).",
  OWNER_EMAILS: "your email address; several are separated by commas.",
};

export function assertEnvironment(env) {
  if (!ENVIRONMENTS.includes(env)) throw new Error(`environment must be staging or production, not "${env}"`);
  return env;
}

/** Key id for a generated key: `<prefix>-<env>-1` the first time, `<prefix>-<env>-r<run number>` on a rotation. */
export function kidFor(prefix, env, { rotation = false, runNumber } = {}) {
  assertEnvironment(env);
  let suffix = "1";
  if (rotation) {
    if (!/^[1-9][0-9]{0,9}$/.test(String(runNumber ?? ""))) throw new Error("a rotation needs the workflow run number for a new key id");
    suffix = `r${runNumber}`;
  }
  const kid = `${prefix}-${KID_ENV[env]}-${suffix}`;
  if (!KID.test(kid)) throw new Error(`key id "${kid}" is not 1 to 32 of A-Z a-z 0-9 . _ -`);
  return kid;
}

/**
 * What to do with each required secret.
 * required: names from wrangler.toml [env.X.secrets].required; existing: names already set on the Worker.
 * Returns { generate, kept, refused, ownerMustEnter, unhandled }. A secret is generated only when it is missing, or
 * when `rotate` is set and its rotation is allowed. Owner secrets are never generated; an unknown name is never
 * guessed at (it is reported as `unhandled`).
 */
export function planSecrets({ env, required, existing, rotate = false, runNumber }) {
  assertEnvironment(env);
  if (!Array.isArray(required) || required.length === 0) throw new Error("wrangler.toml declares no required secrets for this environment");
  const present = new Set(existing);
  const plan = { generate: [], kept: [], refused: [], ownerMustEnter: [], unhandled: [] };
  for (const name of required) {
    const generated = GENERATED_SECRETS[name];
    if (generated) {
      const exists = present.has(name);
      if (exists && !rotate) {
        plan.kept.push(name);
      } else if (exists && generated.rotation) {
        plan.kept.push(name);
        plan.refused.push({ name, reason: generated.rotation });
      } else {
        plan.generate.push({
          name,
          kind: generated.kind,
          purpose: generated.purpose,
          kid: kidFor(generated.kidPrefix, env, { rotation: exists, runNumber }),
          reason: exists ? "rotate" : "missing",
        });
      }
    } else if (name in OWNER_SECRETS) {
      if (present.has(name)) plan.kept.push(name);
      else plan.ownerMustEnter.push({ name, source: OWNER_SECRETS[name] });
    } else if (present.has(name)) {
      plan.kept.push(name);
    } else {
      plan.unhandled.push(name);
    }
  }
  return plan;
}

/** True when every required secret will exist once the plan is applied, so `wrangler deploy` can succeed. */
export function readyToDeploy(plan) {
  return plan.ownerMustEnter.length === 0 && plan.unhandled.length === 0;
}

/**
 * The public key (SPKI, base64url) that `new-secret.mjs signing-key` prints on standard error, read from the
 * TrustedServiceKeys.json line in its notice (trusted-key-entry.mjs publicKeyNotice). The line must name this kid and
 * exactly the purposes the kid implies.
 */
export function publicKeyFromGeneratorOutput(stderr, kid) {
  const marker = `Public key for kid "${kid}" (not secret).`;
  const at = stderr.indexOf(marker);
  if (at < 0) throw new Error(`the key generator printed no public key for "${kid}"`);
  const line = stderr
    .slice(at + marker.length)
    .split("\n")
    .map((l) => l.trim())
    .find((l) => l.startsWith("{"));
  let entry = null;
  try {
    entry = line ? JSON.parse(line) : null;
  } catch {
    entry = null;
  }
  const spki = typeof entry?.spki === "string" ? entry.spki : "";
  const expected = purposeForKid(kid) === null ? [] : purposesFor(purposeForKid(kid));
  const purposeOk = Array.isArray(entry?.purposes) && expected.length > 0 && entry.purposes.length === expected.length && expected.every((p, i) => entry.purposes[i] === p);
  if (entry?.kid !== kid || !purposeOk || !/^[A-Za-z0-9_-]{80,}$/.test(spki)) throw new Error(`the key generator printed an unusable public key for "${kid}"`);
  return spki;
}

/** Every value inside a generated secret that must be masked in the job log: the whole text and each key in it. */
export function valuesToMask(secretText) {
  const values = new Set([secretText]);
  // The signing keys and the invite ring are JSON; BAN_HMAC_KEY is a bare base64url key (the whole text, added above).
  // A JSON-looking value that does not parse is a generator fault and throws, so nothing goes unmasked silently.
  if (secretText.startsWith("{")) {
    const parsed = JSON.parse(secretText);
    if (typeof parsed.pkcs8 === "string") values.add(parsed.pkcs8);
    if (parsed.keys && typeof parsed.keys === "object") for (const key of Object.values(parsed.keys)) if (typeof key === "string") values.add(key);
  }
  return [...values].filter((v) => v.length > 0);
}

/** One TrustedServiceKeys.json entry per generated signing key (src/DbdPresence.Online/TrustedServiceKeys.json). */
export function trustedKeyEntries(generated) {
  return generated.filter((g) => g.purpose && g.spki).map((g) => ({ kid: g.kid, purposes: purposesFor(g.purpose), spki: g.spki }));
}

/** The Cloudflare resources of one environment, read from wrangler.toml (unstable_readConfig output). */
export function resourcesFromConfig(config, env) {
  assertEnvironment(env);
  const d1 = config.d1_databases ?? [];
  if (d1.length !== 1 || d1[0].binding !== "DB" || typeof d1[0].database_name !== "string") throw new Error(`wrangler.toml [env.${env}] must declare exactly one D1 database with binding DB`);
  const buckets = (config.r2_buckets ?? []).map((b) => ({ binding: b.binding, name: b.bucket_name }));
  if (buckets.length === 0 || buckets.some((b) => typeof b.name !== "string" || !b.name.includes(`-${env}-`))) throw new Error(`wrangler.toml [env.${env}] R2 bucket names must be set and contain "-${env}-"`);
  if (!d1[0].database_name.endsWith(`-${env}`)) throw new Error(`the ${env} database name must end in "-${env}"`);
  if (config.workers_dev !== false || config.preview_urls !== false) throw new Error(`wrangler.toml [env.${env}] must switch off workers_dev and preview_urls`);
  return {
    workerName: config.name,
    database: { binding: "DB", name: d1[0].database_name, configuredId: d1[0].database_id ?? null },
    buckets,
    requiredSecrets: config.secrets?.required ?? [],
    routes: config.routes ?? [],
  };
}

/**
 * The per-deploy wrangler config: the raw wrangler.toml content with the resolved D1 database_id added to the chosen
 * environment. Nothing else changes, and the result is never committed (it is written next to wrangler.toml in the
 * job's checkout only, so relative paths such as `main` and `migrations_dir` still resolve).
 */
export function withDatabaseId(rawConfig, env, databaseName, databaseId) {
  assertEnvironment(env);
  if (!UUID.test(databaseId)) throw new Error(`"${databaseId}" is not a D1 database id`);
  const copy = JSON.parse(JSON.stringify(rawConfig));
  const databases = copy.env?.[env]?.d1_databases;
  const entry = Array.isArray(databases) ? databases.find((d) => d.binding === "DB" && d.database_name === databaseName) : undefined;
  if (!entry) throw new Error(`wrangler.toml has no [[env.${env}.d1_databases]] entry DB = ${databaseName}`);
  if (entry.database_id !== undefined && entry.database_id !== databaseId) {
    throw new Error(`wrangler.toml names database id ${entry.database_id} for ${databaseName}, but Cloudflare has ${databaseId}; fix wrangler.toml before deploying`);
  }
  entry.database_id = databaseId;
  return copy;
}

/** Fields that must be identical in wrangler.toml and the generated config (checked after both are read back). */
export function configDifferences(original, generated) {
  const pick = (c) => ({
    name: c.name,
    main: c.main,
    workers_dev: c.workers_dev,
    preview_urls: c.preview_urls,
    routes: c.routes,
    vars: c.vars,
    crons: c.triggers?.crons,
    r2: (c.r2_buckets ?? []).map((b) => [b.binding, b.bucket_name]),
    d1: (c.d1_databases ?? []).map((d) => [d.binding, d.database_name, d.migrations_dir]),
    secrets: c.secrets?.required,
    compatibility_date: c.compatibility_date,
  });
  const a = pick(original);
  const b = pick(generated);
  return Object.keys(a).filter((k) => JSON.stringify(a[k]) !== JSON.stringify(b[k]));
}

/**
 * Adds a rule to an R2 rule list without touching the others (the API replaces the whole list).
 * Returns { action: "keep" | "add" | "conflict", rules }. A rule with the same id but other settings is a conflict:
 * the job reports it and leaves it alone, because a lock rule must never be weakened by automation.
 */
function mergeRule(existing, wanted, same) {
  const rules = Array.isArray(existing) ? existing : [];
  const current = rules.find((r) => r.id === wanted.id);
  if (!current) return { action: "add", rules: [...rules, wanted] };
  return { action: same(current) ? "keep" : "conflict", rules };
}

/** An R2 bucket lock rule: no object of the bucket can be deleted or overwritten until it is `days` days old. */
export function lockRule(id, days) {
  if (typeof id !== "string" || id.length === 0 || !Number.isInteger(days) || days < 1) throw new Error("a lock rule needs an id and a whole number of days");
  return { id, enabled: true, condition: { type: "Age", maxAgeSeconds: days * DAY_SECONDS } };
}

export function auditLockRule() {
  return lockRule(AUDIT_LOCK_RULE_ID, AUDIT_DAYS);
}

/** An R2 lifecycle rule that deletes every object of the bucket `days` days after it was written. */
export function lifecycleRule(id, days) {
  if (typeof id !== "string" || id.length === 0 || !Number.isInteger(days) || days < 1) throw new Error("a lifecycle rule needs an id and a whole number of days");
  return { id, enabled: true, conditions: {}, deleteObjectsTransition: { condition: { type: "Age", maxAge: days * DAY_SECONDS } } };
}

export function auditLifecycleRule() {
  return lifecycleRule(AUDIT_LIFECYCLE_RULE_ID, AUDIT_DAYS);
}

/** Plans `wanted` (the audit bucket's 90-day lock unless another rule is given) into the bucket's lock rules. */
export function planLockRules(existing, wanted = auditLockRule()) {
  return mergeRule(existing, wanted, (r) => r.enabled === true && !r.prefix && r.condition?.type === "Age" && r.condition?.maxAgeSeconds === wanted.condition.maxAgeSeconds);
}

/** Plans `wanted` (the audit bucket's 90-day expiry unless another rule is given) into the bucket's lifecycle rules. */
export function planLifecycleRules(existing, wanted = auditLifecycleRule()) {
  return mergeRule(
    existing,
    wanted,
    (r) => r.enabled === true && !r.conditions?.prefix && r.deleteObjectsTransition?.condition?.type === "Age" && r.deleteObjectsTransition?.condition?.maxAge === wanted.deleteObjectsTransition.condition.maxAge,
  );
}

function decodeBase64Url(text) {
  if (typeof text !== "string" || !/^[A-Za-z0-9_-]*$/.test(text)) throw new Error("not base64url");
  const b64 = text.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (text.length % 4)) % 4);
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Checks a GET /v1/config response body: `{"schemaVersion", "document"}`, the document in the ADR-0012 format with
 * purpose `config`, not expired. With `trustedKeys` ([{kid, spki}]) the signature is verified too; without them the
 * result says so (`signatureChecked: false`) instead of claiming a verified signature.
 */
export async function inspectConfigResponse(bodyText, { now = Date.now(), trustedKeys = [] } = {}) {
  let body;
  try {
    body = JSON.parse(bodyText);
  } catch {
    return { ok: false, reason: "the response is not JSON" };
  }
  if (!body || typeof body !== "object" || typeof body.schemaVersion !== "number") return { ok: false, reason: "the response has no schemaVersion" };
  let outer = body.document;
  if (typeof outer === "string") {
    try {
      outer = JSON.parse(outer);
    } catch {
      return { ok: false, reason: "the document is not JSON" };
    }
  }
  if (!outer || typeof outer.kid !== "string" || typeof outer.signed !== "string" || typeof outer.sig !== "string") return { ok: false, reason: "the document is not a signed document (kid, signed, sig)" };
  let envelopeBytes;
  let envelope;
  try {
    envelopeBytes = decodeBase64Url(outer.signed);
    envelope = JSON.parse(new TextDecoder().decode(envelopeBytes));
  } catch {
    return { ok: false, reason: "the signed envelope cannot be read" };
  }
  if (envelope.purpose !== "config") return { ok: false, reason: `the document's purpose is "${envelope.purpose}", not "config"` };
  if (envelope.kid !== outer.kid) return { ok: false, reason: "the envelope kid differs from the document kid" };
  const expires = Date.parse(envelope.expiresAt);
  if (!Number.isFinite(expires) || expires <= now) return { ok: false, reason: "the document has expired or has no expiry" };
  const result = { ok: true, kid: outer.kid, expiresAt: envelope.expiresAt, signatureChecked: false };
  const key = trustedKeys.find((k) => k.kid === outer.kid);
  if (!key) return result;
  try {
    const publicKey = await crypto.subtle.importKey("spki", decodeBase64Url(key.spki), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    const valid = await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, decodeBase64Url(outer.sig), envelopeBytes);
    if (!valid) return { ok: false, reason: `the signature does not verify with the public key of "${outer.kid}"` };
  } catch {
    return { ok: false, reason: `the signature or the public key of "${outer.kid}" cannot be read` };
  }
  return { ...result, signatureChecked: true };
}

/** Owner-facing name of each environment. */
const ENV_LABEL = { staging: "Staging (the test copy)", production: "Production (the real service)" };

const RESULT_WORD = { pass: "OK", fail: "FAILED", warn: "Not yet", skip: "Not checked" };

/**
 * The job summary in plain English for the owner: what was created, what is live, what to do next, and (for the
 * developer) the public keys. `state` is what scripts/provision.mjs recorded; `outcomes` are the workflow step
 * results (success, failure, skipped, cancelled or empty); `smoke` is [{check, result, detail}].
 */
export function renderSummary({ env, state = {}, outcomes = {}, smoke = [] }) {
  assertEnvironment(env);
  const hosts = HOSTS[env];
  const lines = [];
  const resources = state.resources ?? null;
  const secrets = state.secrets ?? null;
  const workerName = resources?.workerName ?? (env === "staging" ? "dbd-presence-service-staging" : "dbd-presence-service-production");
  const project = state.pages?.project ?? PAGES_PROJECTS[env];
  const deployed = outcomes.deploy === "success";
  const smokeFailed = smoke.some((s) => s.result === "fail");
  const ownerMissing = secrets?.ownerMustEnter ?? [];
  const unhandled = secrets?.unhandled ?? [];

  lines.push(`# ${ENV_LABEL[env]}: deployment report`, "");
  if (outcomes.verify && outcomes.verify !== "success") {
    lines.push("**Nothing was changed.** The automatic checks of the code did not pass, so the workflow stopped before touching Cloudflare. Ask the developer to look at the failed check.", "");
  } else if (deployed && !smokeFailed) {
    lines.push(`**The service is live** at https://${hosts.api}.`, "");
  } else if (deployed) {
    lines.push(`**The service was deployed, but a safety check failed.** See "Checks" below and tell the developer straight away.`, "");
  } else if (ownerMissing.length > 0 || unhandled.length > 0) {
    lines.push(`**Not live yet: the service is waiting for ${ownerMissing.length + unhandled.length} value(s) that only you can enter.** Everything the workflow can create is ready. Enter the values below, then run the workflow again.`, "");
  } else if ((secrets?.notCreated ?? []).length > 0) {
    lines.push("**Not live yet: some keys do not exist and provision was switched off.** Run the workflow again with provision ticked.", "");
  } else {
    lines.push("**The service was not deployed.** A step failed; see the list below and the red step in the log.", "");
  }

  lines.push("## What this run did", "");
  const did = [];
  if (resources) {
    const db = resources.database;
    if (db) did.push(`Database \`${db.name}\`: ${db.created ? "**created**" : "already there"}.`);
    for (const b of resources.buckets ?? []) did.push(`Storage bucket \`${b.name}\`: ${b.created ? "**created**" : "already there"}.`);
    for (const r of resources.rules ?? []) {
      const word = {
        add: "**added**",
        keep: "already set",
        conflict: "**a rule with this name exists with other settings; left unchanged, please check it**",
        missing: "**missing** (run again with provision ticked)",
        failed: `**could not be set automatically** (${r.error ?? "error"}); please set it in the dashboard`,
      }[r.action] ?? "not checked";
      const days = r.days ?? AUDIT_DAYS;
      did.push(`${r.kind === "lock" ? `Lock rule (nothing can be changed or deleted for ${days} days)` : `Expiry rule (exports are deleted after ${days} days)`} \`${r.id}\` on \`${r.bucket}\`: ${word}.`);
    }
  }
  if (outcomes.migrations) did.push(`Database tables (migrations): ${outcomes.migrations === "success" ? "up to date" : outcomes.migrations === "skipped" ? "not applied" : "**failed**"}.`);
  if (secrets) {
    for (const g of secrets.generated ?? []) did.push(`Key \`${g.name}\` (${g.kid}): **${g.reason === "rotate" ? "replaced with a new key" : "generated and stored in Cloudflare"}**. Its value was never shown.`);
    for (const name of secrets.kept ?? []) did.push(`Secret \`${name}\`: already set, left unchanged.`);
    for (const r of secrets.refused ?? []) did.push(`Secret \`${r.name}\`: **not replaced** (${r.reason})`);
  }
  if (state.workersDev) did.push(`The \`workers.dev\` address of the Worker: ${state.workersDev.changed ? "**switched off**" : state.workersDev.enabled === false ? "off" : "not checked"}.`);
  if (state.pages) did.push(`Owner Panel project \`${project}\` on Cloudflare Pages: ${state.pages.created ? "**created**" : "already there"}.`);
  if (did.length === 0) did.push("Nothing (the workflow stopped before changing anything).");
  for (const d of did) lines.push(`- ${d}`);
  lines.push("");

  lines.push("## What is live", "");
  const live = (word) => (word === "success" ? "yes" : word === "skipped" || !word ? "no (step skipped)" : "no (step failed)");
  lines.push(`- Service (\`${workerName}\`) at https://${hosts.api} and https://${hosts.admin}/api/: ${deployed ? "yes" : live(outcomes.deploy)}.`);
  lines.push(`- Owner Panel files on the Pages project \`${project}\`: ${live(outcomes.panel)}.`);
  lines.push("");

  if (smoke.length > 0) {
    lines.push("## Checks", "", "| Check | Result | Detail |", "| --- | --- | --- |");
    for (const s of smoke) lines.push(`| ${s.check} | ${RESULT_WORD[s.result] ?? s.result} | ${String(s.detail ?? "").replace(/\|/g, "\\|")} |`);
    lines.push("");
  }

  lines.push("## What you need to do next", "");
  const todo = [];
  if (ownerMissing.length > 0 || unhandled.length > 0) {
    const where = `In the Cloudflare dashboard open **Workers & Pages** → **${workerName}** → **Settings** → **Variables and Secrets** → **Add**. Choose type **Secret**, type the name exactly as shown, paste the value, and click **Deploy**. Add each one:`;
    const items = ownerMissing.map((m) => `   - \`${m.name}\`: ${m.source}`);
    for (const name of unhandled) items.push(`   - \`${name}\`: this workflow does not know this secret yet. Ask the developer what it should be before entering anything.`);
    todo.push(`${where}\n${items.join("\n")}\n   Then run this workflow again with the same settings.`);
  }
  if ((secrets?.notCreated ?? []).length > 0) {
    todo.push(`Run this workflow again with **provision** ticked, so that it can generate: ${secrets.notCreated.map((n) => `\`${n}\``).join(", ")}.`);
  }
  if (outcomes.panel === "success") {
    todo.push(`If not done yet, attach the panel's address: **Workers & Pages** → **${project}** → **Custom domains** → **Set up a custom domain** → \`${hosts.admin}\` (owner guide step 12.3).`);
    todo.push(`If not done yet, close the other ways in: **Workers & Pages** → **${project}** → **Settings** → **General** → **Access policy** → enable; and in **Zero Trust** → **Access** → **Applications** → the Owner Panel application, add the domain \`${project}.pages.dev\` (owner guide steps 6 and 12.4).`);
  }
  for (const r of resources?.rules ?? []) {
    if (r.action === "conflict" || r.action === "failed" || r.action === "missing") todo.push(`Check the ${r.kind === "lock" ? "bucket lock" : "lifecycle"} rule on \`${r.bucket}\`: **R2 object storage** → \`${r.bucket}\` → **Settings** → **${r.kind === "lock" ? "Bucket lock rules" : "Object lifecycle rules"}**. It must be ${r.days ?? AUDIT_DAYS} days for all objects (${r.guide ?? "owner guide step 7.4"}).`);
  }
  if ((secrets?.generated ?? []).some((g) => g.purpose)) todo.push("Send the developer the public keys below (or the `public-keys` file attached to this run). They are not secret.");
  if ((secrets?.generated ?? []).some((g) => g.reason === "rotate")) todo.push("**Signing keys were replaced.** Copies of the app built with the old public keys will refuse new licences and configuration until a build with the new keys is released.");
  if (env === "production") todo.push("Keep **Settings** → **Environments** → **production** → **Required reviewers** set to yourself in GitHub, so production never deploys without your approval.");
  if (todo.length === 0 && !deployed) todo.push("The service is not live. Open the red step in the log to see what stopped it, and tell the developer.");
  if (todo.length === 0) todo.push("Nothing. Everything this workflow manages is in place.");
  todo.forEach((t, i) => lines.push(`${i + 1}. ${t}`));
  lines.push("");

  const keys = trustedKeyEntries(secrets?.generated ?? []);
  lines.push("## For the developer: public keys", "");
  if (keys.length > 0) {
    lines.push(`New public keys for the \`${env}\` list in \`src/DbdPresence.Online/TrustedServiceKeys.json\` (replace any entry with the same kid, including a reserved \`"awaitingKey": true\` entry such as \`pack-index-staging-1\`, which then loses \`awaitingKey\`). Also attached as \`public-keys-${env}.json\`. Private keys were never written to the log or to a file.`, "");
    lines.push("```json", JSON.stringify(keys, null, 2), "```", "");
  } else {
    lines.push("No new keys this run. The public keys from the run that generated them are still the right ones.", "");
  }
  return lines.join("\n");
}

/** Parses the smoke-check file written by the workflow: one `check<TAB>result<TAB>detail` line per check. */
export function parseSmokeResults(text) {
  return String(text ?? "")
    .split("\n")
    .filter((line) => line.trim().length > 0)
    .map((line) => {
      const [check = "", result = "", ...detail] = line.split("\t");
      return { check, result, detail: detail.join(" ") };
    });
}
