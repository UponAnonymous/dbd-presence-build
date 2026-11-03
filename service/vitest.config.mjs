// Tests run inside the Workers runtime (Miniflare) with the production environment's configuration from wrangler.toml,
// a local D1 database and test-only secrets. Nothing here contacts Cloudflare or Discord.
// The signing key is the public TEST key in tests/fixtures/signing (never trusted by a release build); the invite
// HMAC key is random per run. The offline release and recovery keys (V2-S6) are made fresh for every run and never written
// anywhere: the Worker gets only their public halves (OFFLINE_PUBLIC_KEYS), the tests get the private halves to sign with.

import { generateKeyPairSync, randomBytes } from "node:crypto";
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-plugin";
import { unstable_readConfig } from "wrangler";
import { defineConfig } from "vitest/config";

const fixtures = join(import.meta.dirname, "..", "tests", "fixtures", "signing");

function pemToBase64Url(pem) {
  const body = pem.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
  return Buffer.from(body, "base64").toString("base64url");
}

function readJsonDir(dir) {
  return readdirSync(dir)
    .filter((f) => f.endsWith(".json"))
    .sort()
    .map((f) => ({ file: f, text: readFileSync(join(dir, f), "utf8") }));
}

function offlineKey(kid, purpose) {
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  return {
    kid,
    purposes: [purpose],
    spki: publicKey.export({ type: "spki", format: "der" }).toString("base64url"),
    pkcs8: privateKey.export({ type: "pkcs8", format: "der" }).toString("base64url"),
  };
}

function sourceFiles(dir) {
  return readdirSync(dir, { recursive: true })
    .filter((f) => String(f).endsWith(".ts"))
    .sort()
    .map((f) => ({ file: String(f).replaceAll("\\", "/"), text: readFileSync(join(dir, String(f)), "utf8") }));
}

function wranglerSummary(env) {
  const config = unstable_readConfig({ config: join(import.meta.dirname, "wrangler.toml"), env });
  return {
    name: config.name,
    workersDev: config.workers_dev,
    previewUrls: config.preview_urls,
    routes: config.routes,
    vars: config.vars,
    d1: config.d1_databases.map((d) => ({ binding: d.binding, name: d.database_name, id: d.database_id ?? null })),
    r2: config.r2_buckets.map((b) => ({ binding: b.binding, name: b.bucket_name })),
    secrets: config.secrets?.required ?? [],
    crons: config.triggers?.crons ?? [],
  };
}

export default defineConfig(async () => {
  const testSigningKey = JSON.stringify({
    kid: "test-content-1",
    pkcs8: pemToBase64Url(readFileSync(join(fixtures, "keys", "test-content-1.private.pem"), "utf8")),
  });
  const inviteKeys = JSON.stringify({
    current: "test-2",
    keys: { "test-1": randomBytes(32).toString("base64url"), "test-2": randomBytes(32).toString("base64url") },
  });
  const migrations = await readD1Migrations(join(import.meta.dirname, "migrations"));
  const offlineKeys = [offlineKey("release-test-1", "releases"), offlineKey("recovery-test-1", "key-replacement")];

  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: "./wrangler.toml", environment: "production" },
        miniflare: {
          bindings: {
            TEST_MIGRATIONS: migrations,
            DISCORD_CLIENT_SECRET: "test-client-secret",
            INVITE_HMAC_KEYS: inviteKeys,
            BAN_HMAC_KEY: randomBytes(32).toString("base64url"),
            LICENCE_SIGNING_KEY: testSigningKey,
            CONFIG_SIGNING_KEY: testSigningKey,
            CONTENT_SIGNING_KEY: testSigningKey,
            ACCESS_AUD: "test-access-aud",
            ACCESS_TEAM_DOMAIN: "https://dbdp-test.cloudflareaccess.com",
            OWNER_EMAILS: "owner@example.com, second.owner@example.com",
            OFFLINE_PUBLIC_KEYS: JSON.stringify(offlineKeys.map(({ kid, purposes, spki }) => ({ kid, purposes, spki }))),
          },
        },
      }),
    ],
    test: {
      // The 400-upload attempt-counter test and the CPU measurements can pass the 5 s default on a loaded runner (round-2 review I4, QA F8).
      testTimeout: 60_000,
      setupFiles: ["./test/setup.ts"],
      provide: {
        signingKeys: readFileSync(join(fixtures, "keys", "keys.json"), "utf8"),
        signingVectors: readJsonDir(join(fixtures, "vectors")),
        crossStackDocuments: readJsonDir(join(fixtures, "ts-signed")),
        publishedIndexFixture: readFileSync(join(fixtures, "..", "packs", "published-index.json"), "utf8"),
        uploadsContract: readFileSync(join(fixtures, "..", "uploads", "contract.json"), "utf8"),
        rosterSeed: readFileSync(join(fixtures, "..", "roster", "seed.json"), "utf8"),
        rosterFoldCases: readFileSync(join(fixtures, "..", "roster", "fold-cases.json"), "utf8"),
        signedRoster: readFileSync(join(fixtures, "..", "roster", "signed-roster.json"), "utf8"),
        vocabulary: readFileSync(join(import.meta.dirname, "..", "src", "DbdPresence.Vision", "Vocabulary", "vocabulary.en.json"), "utf8"),
        migrationNames: readdirSync(join(import.meta.dirname, "migrations")).sort(),
        ownerGuide: readFileSync(join(import.meta.dirname, "..", "docs", "owner", "cloudflare-setup.md"), "utf8"),
        ciServiceWorkflow: readFileSync(join(import.meta.dirname, "..", ".github", "workflows", "ci-service.yml"), "utf8"),
        backupWorkflow: readFileSync(join(import.meta.dirname, "..", ".github", "workflows", "backup-service.yml"), "utf8"),
        backupScript: readFileSync(join(import.meta.dirname, "scripts", "backup.mjs"), "utf8"),
        backupGuide: readFileSync(join(import.meta.dirname, "..", "docs", "owner", "backups.md"), "utf8"),
        privacyNoticeMarkdown: readFileSync(join(import.meta.dirname, "..", "docs", "legal", "privacy-notice.md"), "utf8"),
        termsMarkdown: readFileSync(join(import.meta.dirname, "..", "docs", "legal", "terms-of-use.md"), "utf8"),
        legalPagesModule: readFileSync(join(import.meta.dirname, "src", "legal-pages.ts"), "utf8"),
        offlineSigningKeys: JSON.stringify(offlineKeys),
        serviceSources: sourceFiles(join(import.meta.dirname, "src")),
        trustedServiceKeys: readFileSync(join(import.meta.dirname, "..", "src", "DbdPresence.Online", "TrustedServiceKeys.json"), "utf8"),
        wranglerProduction: wranglerSummary("production"),
        wranglerStaging: wranglerSummary("staging"),
      },
    },
  };
});
