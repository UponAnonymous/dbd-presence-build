// The deploy workflow's rules (scripts/provision-plan.mjs, used by scripts/provision.mjs and
// .github/workflows/deploy-service.yml): FR-ADM-05 (audit lock), FR-ADM-06 (separate environments), FR-ADM-02 and
// gate V3.4 (no workers.dev), NFR-SVC-04 (signed config), NFR-SVC-05 (keys are Worker secrets, none printed).

import { describe, expect, inject, it } from "vitest";
import {
  auditLifecycleRule,
  auditLockRule,
  configDifferences,
  inspectConfigResponse,
  kidFor,
  parseSmokeResults,
  planLifecycleRules,
  planLockRules,
  planSecrets,
  publicKeyFromGeneratorOutput,
  readyToDeploy,
  renderSummary,
  trustedKeyEntries,
  valuesToMask,
  withDatabaseId,
} from "../scripts/provision-plan.mjs";
import { publicKeyNotice, trustedKeyEntry } from "../scripts/trusted-key-entry.mjs";
import { encodeBase64Url } from "../src/lib/base64url.ts";
import { signDocument } from "../src/signing/document.ts";

const staging = inject("wranglerStaging");
const production = inject("wranglerProduction");
const DB_ID = "3f2a1b4c-5d6e-4f70-8192-a3b4c5d6e7f8";

describe("secret plan", () => {
  it("generates the five keys for a new staging Worker and leaves the owner's five values to the owner", () => {
    const plan = planSecrets({ env: "staging", required: staging.secrets, existing: [] });
    expect(plan.generate.map((g) => [g.name, g.kid, g.kind])).toEqual([
      ["INVITE_HMAC_KEYS", "invite-staging-1", "invite-keys"],
      ["BAN_HMAC_KEY", "ban-staging-1", "ban-key"],
      ["LICENCE_SIGNING_KEY", "licence-staging-1", "signing-key"],
      ["CONFIG_SIGNING_KEY", "config-staging-1", "signing-key"],
      ["CONTENT_SIGNING_KEY", "pack-index-staging-1", "signing-key"],
    ]);
    expect(plan.generate.find((g) => g.name === "CONTENT_SIGNING_KEY")!.purpose).toBe("pack-index");
    expect(plan.ownerMustEnter.map((o) => o.name).sort()).toEqual(["ACCESS_AUD", "ACCESS_TEAM_DOMAIN", "DISCORD_CLIENT_ID", "DISCORD_CLIENT_SECRET", "OWNER_EMAILS"]);
    expect(readyToDeploy(plan)).toBe(false);
  });

  it("uses the production key names from the owner guide, and the production client ID is not a secret", () => {
    const plan = planSecrets({ env: "production", required: production.secrets, existing: [] });
    expect(plan.generate.map((g) => g.kid)).toEqual(["invite-prod-1", "ban-prod-1", "licence-prod-1", "config-prod-1", "pack-index-prod-1"]);
    expect(plan.ownerMustEnter.map((o) => o.name)).not.toContain("DISCORD_CLIENT_ID");
  });

  it("never regenerates an existing secret without rotate", () => {
    const plan = planSecrets({ env: "production", required: production.secrets, existing: production.secrets });
    expect(plan.generate).toEqual([]);
    expect(plan.kept.sort()).toEqual([...production.secrets].sort());
    expect(readyToDeploy(plan)).toBe(true);
  });

  it("on rotate replaces only the signing keys, with new kids, and refuses the ban key and the invite ring", () => {
    const plan = planSecrets({ env: "production", required: production.secrets, existing: production.secrets, rotate: true, runNumber: 42 });
    expect(plan.generate.map((g) => [g.name, g.kid, g.reason])).toEqual([
      ["LICENCE_SIGNING_KEY", "licence-prod-r42", "rotate"],
      ["CONFIG_SIGNING_KEY", "config-prod-r42", "rotate"],
      ["CONTENT_SIGNING_KEY", "pack-index-prod-r42", "rotate"],
    ]);
    expect(plan.refused.map((r) => r.name)).toEqual(["INVITE_HMAC_KEYS", "BAN_HMAC_KEY"]);
    expect(plan.refused.find((r) => r.name === "BAN_HMAC_KEY")!.reason).toMatch(/never rotated/);
    expect(plan.kept).toContain("BAN_HMAC_KEY");
    expect(plan.generate.map((g) => g.name)).not.toContain("BAN_HMAC_KEY");
  });

  it("never generates owner values, even on rotate", () => {
    const plan = planSecrets({ env: "staging", required: staging.secrets, existing: ["BAN_HMAC_KEY"], rotate: true, runNumber: 7 });
    for (const name of ["DISCORD_CLIENT_SECRET", "DISCORD_CLIENT_ID", "ACCESS_AUD", "ACCESS_TEAM_DOMAIN", "OWNER_EMAILS"]) expect(plan.generate.map((g) => g.name)).not.toContain(name);
    expect(plan.generate.map((g) => g.name)).not.toContain("BAN_HMAC_KEY");
  });

  it("reports a required secret it does not know instead of guessing, and does not deploy without it", () => {
    const plan = planSecrets({ env: "staging", required: [...staging.secrets, "FUTURE_UNKNOWN_SECRET"], existing: staging.secrets });
    expect(plan.unhandled).toEqual(["FUTURE_UNKNOWN_SECRET"]);
    expect(plan.generate).toEqual([]);
    expect(readyToDeploy(plan)).toBe(false);
  });

  it("refuses a rotation without a run number and bad environments", () => {
    expect(() => planSecrets({ env: "staging", required: staging.secrets, existing: staging.secrets, rotate: true })).toThrow(/run number/);
    expect(() => planSecrets({ env: "prod", required: staging.secrets, existing: [] })).toThrow(/staging or production/);
    expect(() => kidFor("licence", "staging", { rotation: true, runNumber: "1; rm -rf /" })).toThrow();
    expect(kidFor("licence", "staging", { rotation: true, runNumber: 9_999_999_999 }).length).toBeLessThanOrEqual(32);
  });
});

describe("generated keys", () => {
  it("reads the public key the generator prints and masks every private part", () => {
    const spki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjZVqOyLuyR_k1wyFiM9-4WQWN0SH7qwbWkXHNWeYVXTH0-FAJNbDfNVyaBcW0SbpfdeRa1Hg0mWC1RS-e9OnDA";
    // The exact notice scripts/new-secret.mjs prints (it calls publicKeyNotice too).
    expect(publicKeyFromGeneratorOutput(`${publicKeyNotice("config-staging-1", spki)}\n`, "config-staging-1")).toBe(spki);
    expect(publicKeyFromGeneratorOutput(publicKeyNotice("pack-index-prod-r42", spki), "pack-index-prod-r42")).toBe(spki);
    expect(() => publicKeyFromGeneratorOutput("Invite HMAC key generated.", "config-staging-1")).toThrow();
    expect(() => publicKeyFromGeneratorOutput(publicKeyNotice("config-staging-1", spki), "licence-staging-1")).toThrow();
    expect(() => publicKeyFromGeneratorOutput(publicKeyNotice("config-staging-1", "short"), "config-staging-1")).toThrow(/unusable/);
    const wrongPurpose = `Public key for kid "config-staging-1" (not secret). Add this line:\n    ${trustedKeyEntry("config-staging-1", "licence", spki)}`;
    expect(() => publicKeyFromGeneratorOutput(wrongPurpose, "config-staging-1")).toThrow(/unusable/);
    expect(valuesToMask(JSON.stringify({ kid: "k", pkcs8: "PRIVATE" }))).toEqual([JSON.stringify({ kid: "k", pkcs8: "PRIVATE" }), "PRIVATE"]);
    expect(valuesToMask(JSON.stringify({ current: "a", keys: { a: "K1", b: "K2" } }))).toContain("K2");
    expect(valuesToMask("bare-ban-key")).toEqual(["bare-ban-key"]);
    expect(() => valuesToMask("{broken")).toThrow();
  });

  it("lists only signing keys, in the TrustedServiceKeys.json shape", () => {
    expect(
      trustedKeyEntries([
        { name: "BAN_HMAC_KEY", kid: "ban-staging-1", purpose: null, reason: "missing", spki: null },
        { name: "CONFIG_SIGNING_KEY", kid: "config-staging-1", purpose: "config", reason: "missing", spki: "SPKI" },
        { name: "CONTENT_SIGNING_KEY", kid: "pack-index-staging-1", purpose: "pack-index", reason: "missing", spki: "SPKI2" },
      ]),
    ).toEqual([
      { kid: "config-staging-1", purposes: ["config"], spki: "SPKI" },
      { kid: "pack-index-staging-1", purposes: ["pack-index", "roster"], spki: "SPKI2" },
    ]);
  });
});

describe("generated wrangler config", () => {
  const raw = {
    name: "dbd-presence-service",
    env: {
      staging: { name: "dbd-presence-service-staging", d1_databases: [{ binding: "DB", database_name: "dbd-presence-staging", migrations_dir: "migrations" }] },
      production: { name: "dbd-presence-service-production", d1_databases: [{ binding: "DB", database_name: "dbd-presence-production", migrations_dir: "migrations" }] },
    },
  };

  it("adds the database ID to the chosen environment only, without changing the input", () => {
    const out = withDatabaseId(raw, "staging", "dbd-presence-staging", DB_ID) as typeof raw & { env: { staging: { d1_databases: { database_id?: string }[] } } };
    expect(out.env.staging.d1_databases[0]!.database_id).toBe(DB_ID);
    expect((out.env.production.d1_databases[0] as { database_id?: string }).database_id).toBeUndefined();
    expect((raw.env.staging.d1_databases[0] as { database_id?: string }).database_id).toBeUndefined();
  });

  it("refuses a wrong name, a non-ID and a committed ID that disagrees with Cloudflare", () => {
    expect(() => withDatabaseId(raw, "staging", "dbd-presence-production", DB_ID)).toThrow();
    expect(() => withDatabaseId(raw, "staging", "dbd-presence-staging", "not-an-id")).toThrow();
    const committed = withDatabaseId(raw, "staging", "dbd-presence-staging", DB_ID);
    expect(() => withDatabaseId(committed, "staging", "dbd-presence-staging", "00000000-0000-4000-8000-000000000000")).toThrow(/fix wrangler.toml/);
    expect(() => withDatabaseId(committed, "staging", "dbd-presence-staging", DB_ID)).not.toThrow();
  });

  it("notices any difference that matters between wrangler.toml and the generated copy", () => {
    const base = { name: "w", main: "/s/src/index.ts", routes: [{ pattern: "api-staging.arcavale.com", custom_domain: true }], workers_dev: false, vars: { A: "1" } };
    expect(configDifferences(base, { ...base, d1_databases: [] })).toEqual([]);
    expect(configDifferences(base, { ...base, workers_dev: true, routes: [] })).toEqual(["workers_dev", "routes"]);
  });
});

describe("audit bucket rules", () => {
  it("adds the 90-day lock and expiry next to existing rules", () => {
    const existing = [{ id: "Default Multipart Abort Rule", enabled: true, conditions: {}, abortMultipartUploadsTransition: { condition: { type: "Age", maxAge: 604800 } } }];
    const lifecycle = planLifecycleRules(existing);
    expect(lifecycle.action).toBe("add");
    expect(lifecycle.rules).toEqual([...existing, auditLifecycleRule()]);
    expect(auditLifecycleRule()).toMatchObject({ deleteObjectsTransition: { condition: { type: "Age", maxAge: 90 * 86400 } } });
    const lock = planLockRules([]);
    expect(lock).toEqual({ action: "add", rules: [auditLockRule()] });
    expect(auditLockRule()).toMatchObject({ enabled: true, condition: { type: "Age", maxAgeSeconds: 90 * 86400 } });
    expect(auditLockRule()).not.toHaveProperty("prefix");
  });

  it("keeps a matching rule and never changes one with other settings", () => {
    expect(planLockRules([auditLockRule()]).action).toBe("keep");
    const shorter = { ...auditLockRule(), condition: { type: "Age", maxAgeSeconds: 86400 } };
    expect(planLockRules([shorter])).toEqual({ action: "conflict", rules: [shorter] });
    expect(planLifecycleRules(undefined).action).toBe("add");
  });
});

describe("GET /v1/config smoke check", () => {
  async function signed(kid: string, options: { expiresInMs?: number; purpose?: "config" | "licence" } = {}) {
    const pair = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
    const spki = encodeBase64Url(new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer));
    const now = Date.now();
    const document = await signDocument(options.purpose ?? "config", "stable", 1, { banner: null }, pair.privateKey, kid, {
      issuedAt: new Date(now - 1000),
      expiresAt: new Date(now + (options.expiresInMs ?? 60_000)),
    });
    return { body: JSON.stringify({ schemaVersion: 1, document }), spki };
  }

  it("accepts a signed config document and verifies it with this run's public key", async () => {
    const { body, spki } = await signed("config-staging-1");
    expect(await inspectConfigResponse(body)).toMatchObject({ ok: true, kid: "config-staging-1", signatureChecked: false });
    expect(await inspectConfigResponse(body, { trustedKeys: [{ kid: "config-staging-1", spki }] })).toMatchObject({ ok: true, signatureChecked: true });
  });

  it("rejects a wrong key, the wrong purpose, an expired document and non-documents", async () => {
    const { body } = await signed("config-staging-1");
    const other = await signed("config-staging-1");
    expect(await inspectConfigResponse(body, { trustedKeys: [{ kid: "config-staging-1", spki: other.spki }] })).toMatchObject({ ok: false });
    expect((await inspectConfigResponse((await signed("licence-staging-1", { purpose: "licence" })).body)).ok).toBe(false);
    expect((await inspectConfigResponse(body, { now: Date.now() + 120_000 })).ok).toBe(false);
    expect((await inspectConfigResponse('{"error":"internal"}')).ok).toBe(false);
    expect((await inspectConfigResponse("<html>")).ok).toBe(false);
  });
});

describe("job summary", () => {
  it("tells the owner exactly where to enter the missing values and that nothing is live yet", () => {
    const plan = planSecrets({ env: "staging", required: staging.secrets, existing: [] });
    const text = renderSummary({
      env: "staging",
      state: {
        resources: { workerName: "dbd-presence-service-staging", database: { name: "dbd-presence-staging", id: DB_ID, created: true }, buckets: [{ name: "dbd-presence-staging-audit", created: true }], rules: [{ kind: "lock", bucket: "dbd-presence-staging-audit", id: "audit-90-days", action: "failed", error: "HTTP 403" }] },
        secrets: {
          generated: [
            { name: "CONFIG_SIGNING_KEY", kid: "config-staging-1", purpose: "config", reason: "missing", spki: "SPKI" },
            { name: "CONTENT_SIGNING_KEY", kid: "pack-index-staging-1", purpose: "pack-index", reason: "missing", spki: "SPKI2" },
          ],
          kept: [],
          refused: [],
          ownerMustEnter: plan.ownerMustEnter,
          unhandled: [],
        },
        pages: { project: "dbd-presence-panel-staging", created: true },
      },
      outcomes: { verify: "success", deploy: "skipped", panel: "success", migrations: "success" },
      smoke: parseSmokeResults("appeal.arcavale.com unchanged\tpass\t207.244.229.132\n"),
    });
    expect(text).toMatch(/Not live yet/);
    expect(text).toContain("**Workers & Pages** → **dbd-presence-service-staging** → **Settings** → **Variables and Secrets** → **Add**");
    for (const name of ["DISCORD_CLIENT_ID", "DISCORD_CLIENT_SECRET", "ACCESS_AUD", "ACCESS_TEAM_DOMAIN", "OWNER_EMAILS"]) expect(text).toContain(`\`${name}\``);
    expect(text).toContain("Custom domains");
    expect(text).toContain("Bucket lock rules");
    expect(text).toContain('"kid": "config-staging-1"');
    expect(text).toContain("Key `CONTENT_SIGNING_KEY` (pack-index-staging-1): **generated and stored in Cloudflare**");
    expect(text).toContain(JSON.stringify([{ kid: "config-staging-1", purposes: ["config"], spki: "SPKI" }, { kid: "pack-index-staging-1", purposes: ["pack-index", "roster"], spki: "SPKI2" }], null, 2));
    expect(text).toContain("awaitingKey");
    expect(text).toContain("| appeal.arcavale.com unchanged | OK | 207.244.229.132 |");
  });

  it("says plainly when the code checks failed and nothing was changed", () => {
    expect(renderSummary({ env: "production", outcomes: { verify: "failure" } })).toMatch(/Nothing was changed/);
  });

  it("asks for a required reviewer on production and reports a live service only when it is", () => {
    const text = renderSummary({ env: "production", state: { secrets: { generated: [], kept: production.secrets, refused: [], ownerMustEnter: [], unhandled: [] } }, outcomes: { verify: "success", deploy: "success", panel: "success" } });
    expect(text).toMatch(/The service is live\*\* at https:\/\/api\.arcavale\.com/);
    expect(text).toContain("Required reviewers");
    const failed = renderSummary({ env: "production", outcomes: { verify: "success", deploy: "success" }, smoke: [{ check: "workers.dev", result: "fail", detail: "200" }] });
    expect(failed).toMatch(/a safety check failed/);
  });
});
