// FR-ADM-06 (separate environments), FR-ADM-02 (no workers.dev or preview bypass), NFR-SVC-05 (secrets declared,
// none committed), NFR-SVC-06 (forward-only migrations applied by Miniflare).

import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { inject } from "vitest";
import { parseProvider } from "../src/routes/study.ts";

const production = inject("wranglerProduction");
const staging = inject("wranglerStaging");

const SECRETS = [
  "DISCORD_CLIENT_SECRET",
  "INVITE_HMAC_KEYS",
  "BAN_HMAC_KEY",
  "LICENCE_SIGNING_KEY",
  "CONFIG_SIGNING_KEY",
  "CONTENT_SIGNING_KEY",
  "ACCESS_AUD",
  "ACCESS_TEAM_DOMAIN",
  "OWNER_EMAILS",
];

describe("wrangler.toml", () => {
  it("switches off workers.dev and preview URLs in both environments", () => {
    for (const config of [production, staging]) {
      expect(config.workersDev).toBe(false);
      expect(config.previewUrls).toBe(false);
    }
  });

  it("routes production to api.arcavale.com, dbdadmin.arcavale.com/api/* and the whole cdn.arcavale.com host", () => {
    expect(production.routes).toEqual([
      { pattern: "api.arcavale.com", custom_domain: true },
      { pattern: "dbdadmin.arcavale.com/api/*", zone_name: "arcavale.com" },
      { pattern: "cdn.arcavale.com", custom_domain: true },
    ]);
    expect(production.vars).toMatchObject({
      ENVIRONMENT: "production",
      API_HOST: "api.arcavale.com",
      ADMIN_HOST: "dbdadmin.arcavale.com",
      CDN_HOST: "cdn.arcavale.com",
      DISCORD_CLIENT_ID: "1563291325107937340",
    });
  });

  it("routes staging to its own hosts", () => {
    expect(staging.routes).toEqual([
      { pattern: "api-staging.arcavale.com", custom_domain: true },
      { pattern: "admin-staging.arcavale.com/api/*", zone_name: "arcavale.com" },
      { pattern: "cdn-staging.arcavale.com", custom_domain: true },
    ]);
    expect(staging.vars).toMatchObject({ ENVIRONMENT: "staging", API_HOST: "api-staging.arcavale.com", ADMIN_HOST: "admin-staging.arcavale.com", CDN_HOST: "cdn-staging.arcavale.com" });
  });

  it("holds the labelling provider in a valid Worker variable, not in the source (the bundle stays free of provider text)", () => {
    for (const config of [production, staging]) {
      const provider = parseProvider((config.vars as Record<string, string>)["STUDY_LABELLER_PROVIDER"]);
      expect(provider).not.toBeNull();
      expect(provider!.url.startsWith("https://")).toBe(true);
    }
    for (const file of inject("serviceSources")) expect(file.text, file.file).not.toMatch(/claude|anthropic/i);
  });

  it("keeps staging and production data fully separate", () => {
    expect(production.name).not.toBe(staging.name);
    expect(production.d1.map((d) => d.binding)).toEqual(["DB"]);
    expect(staging.d1.map((d) => d.binding)).toEqual(["DB"]);
    expect(production.d1[0]!.name).toBe("dbd-presence-production");
    expect(staging.d1[0]!.name).toBe("dbd-presence-staging");
    expect(production.r2).toEqual([
      { binding: "PACKS", name: "dbd-presence-production-packs" },
      { binding: "UPLOADS", name: "dbd-presence-production-uploads" },
      { binding: "AUDIT_EXPORT", name: "dbd-presence-production-audit" },
      { binding: "RELEASES", name: "dbd-presence-production-releases" },
    ]);
    expect(staging.r2).toEqual([
      { binding: "PACKS", name: "dbd-presence-staging-packs" },
      { binding: "UPLOADS", name: "dbd-presence-staging-uploads" },
      { binding: "AUDIT_EXPORT", name: "dbd-presence-staging-audit" },
      { binding: "RELEASES", name: "dbd-presence-staging-releases" },
    ]);
  });

  it("declares every secret by name only, in both environments", () => {
    for (const config of [production, staging]) {
      for (const name of SECRETS) {
        expect(config.secrets).toContain(name);
        expect(config.vars).not.toHaveProperty(name);
      }
    }
    expect(staging.secrets).toContain("DISCORD_CLIENT_ID");
  });

  it("requires the content signing key now that the pack index is signed (V2-S4)", () => {
    for (const config of [production, staging]) expect(config.secrets).toContain("CONTENT_SIGNING_KEY");
  });

  it("runs the audit export and the retention job daily", () => {
    expect(production.crons).toEqual(["23 3 * * *"]);
    expect(staging.crons).toEqual(["23 3 * * *"]);
  });
});

describe("owner guide (docs/owner/cloudflare-setup.md)", () => {
  const guide = inject("ownerGuide");

  it("covers registrar lock, DNSSEC, Namecheap 2FA and Always Use HTTPS", () => {
    expect(guide).toMatch(/Domain Lock/);
    expect(guide).toMatch(/Enable DNSSEC/);
    expect(guide).toMatch(/DS record/);
    expect(guide).toMatch(/Namecheap[\s\S]{0,200}Two-Factor/);
    expect(guide).toMatch(/Always Use HTTPS/);
  });

  it("extends the WAF rule to the whole API host, every method", () => {
    expect(guide).toContain('(http.host in {"api.arcavale.com" "api-staging.arcavale.com"})');
    expect(guide).not.toContain('http.request.method eq "POST" and http.request.uri.path in {"/v1/auth/discord"');
  });

  it("backs keys up offline, rotates invite keys by merging, never rotates the ban key, stores the content key", () => {
    expect(guide).toMatch(/Back up every key offline before storing it/);
    expect(guide).toContain("rotate-invite-keys");
    expect(guide).toMatch(/\{"current": "<name>", "keys": \{"<old name>": "<key>", "<new name>": "<key>"\}\}/);
    expect(guide).toMatch(/BAN_HMAC_KEY[\s\S]{0,200}Never rotate it/);
    // The content key is named pack-index-*: the app reserves pack-index-staging-1 (awaitingKey until it is pasted in).
    expect(guide).toContain("signing-key pack-index-prod-1 --out E:\\dbdp-keys\\pack-index-prod-1.json | npx wrangler secret put CONTENT_SIGNING_KEY --env production");
    expect(guide).toMatch(/pack-index-staging-1[\s\S]{0,300}awaitingKey/);
    expect(guide).toContain("docs/owner/takedown-runbook.md");
  });

  it("confirms the production Discord application and locks the audit buckets for 90 days", () => {
    expect(guide).toMatch(/your own Discord application \*\*Dead by Daylight\*\*, application ID `1563291325107937340`/);
    expect(guide).toContain("--retention-days 90");
    expect(guide).toContain("dbd-presence-production-audit");
  });
});

describe("CI", () => {
  it("runs the service workflow, and with it the audit gate, on a weekly schedule", () => {
    expect(inject("ciServiceWorkflow")).toMatch(/\n  schedule:\n    - cron: '\d+ \d+ \* \* [0-6]'\n/);
  });
});

describe("D1 migrations", () => {
  it("are numbered, forward-only files", () => {
    const names = inject("migrationNames");
    expect(names.length).toBeGreaterThan(0);
    names.forEach((name, i) => expect(name).toMatch(new RegExp(`^${String(i + 1).padStart(4, "0")}_[a-z0-9_]+\\.sql$`)));
  });

  it("create the expected tables in the Miniflare database", async () => {
    const { results } = await env.DB.prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '_cf_%' AND name <> 'd1_migrations' ORDER BY name").all<{
      name: string;
    }>();
    expect(results.map((r) => r.name)).toEqual([
      "audit_export_state",
      "audit_log",
      "backup_runs",
      "banned_subjects",
      "devices",
      "discord_apps",
      "invites",
      "key_replacements",
      "pack_entries",
      "pack_index",
      "pack_objects",
      "packs",
      "proof_nonces",
      "published_entries",
      "published_packs",
      "rate_limits",
      "refresh_tokens",
      "release_feed_packages",
      "release_feeds",
      "release_packages",
      "roster_blocks",
      "roster_characters",
      "roster_sightings",
      "roster_versions",
      "settings",
      "storage_objects",
      "storage_usage",
      "token_families",
      "uploads",
      "users",
    ]);
  });

  it("enforce foreign keys (the device limit and cascading deletes rely on them)", async () => {
    await expect(
      env.DB.prepare("INSERT INTO devices (id, user_id, public_key_jwk, thumbprint, created_at, last_seen_at) VALUES ('d', 'missing', '{}', 't', 0, 0)").run(),
    ).rejects.toThrow(/FOREIGN KEY/);
  });

  it("seed the access mode as invite-only", async () => {
    const row = await env.DB.prepare("SELECT value FROM settings WHERE key = 'access_mode'").first<{ value: string }>();
    expect(JSON.parse(row!.value)).toBe("invite-only");
  });
});
