// NFR-SVC-03 (rate limits with a reserved write budget; budget exhaustion test), NFR-SVC-07 (no clear IPs), FR-ONL-14
// (upload limits sized for a whole pack).

import { env } from "cloudflare:test";
import { beforeEach, describe, expect, it } from "vitest";
import { handleRequest } from "../src/app.ts";
import { encodeBase64Url } from "../src/lib/base64url.ts";
import { ACCOUNT_LIMITS, D1_DAILY_WRITE_LIMIT, DAY_MS, LIMITS, REUSE_ROWS, SENSITIVE_WRITE_BUDGET, accountRouteDailyWrites, accountsWithinBudget, fixedDailyWrites, ipPrincipal, perAccountDailyWrites, type RouteClass, worstCaseDailyWrites } from "../src/ratelimit.ts";
import { DEFAULT_SETTINGS, DEVICE_LIMIT_MAX } from "../src/db/settings.ts";
import { API, Harness, REDIRECT, newDevice, resetDatabase, rowCount } from "./helpers.ts";
import { png } from "./images.ts";

let h: Harness;
beforeEach(async () => {
  await resetDatabase();
  h = new Harness();
});

const UNWRAP = Symbol("unwrap");

/** Wraps D1 so every row written through it is counted (meta.rows_written of each run and batch result). */
function countingDb(db: D1Database, counter: { rows: number }): D1Database {
  const wrapStatement = (statement: D1PreparedStatement): D1PreparedStatement =>
    new Proxy(statement, {
      get(target, property) {
        if (property === UNWRAP) return target;
        if (property === "bind") return (...values: unknown[]) => wrapStatement(target.bind(...values));
        if (property === "run" || property === "all") {
          return async () => {
            const result = await (target[property] as () => Promise<D1Result>).call(target);
            counter.rows += result.meta.rows_written ?? 0;
            return result;
          };
        }
        const value = Reflect.get(target, property, target) as unknown;
        return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
      },
    });
  return new Proxy(db, {
    get(target, property) {
      if (property === "prepare") return (query: string) => wrapStatement(target.prepare(query));
      if (property === "batch") {
        return async (statements: D1PreparedStatement[]) => {
          const results = await target.batch(statements.map((s) => ((s as unknown as Record<symbol, D1PreparedStatement>)[UNWRAP] ?? s)));
          for (const r of results) counter.rows += r.meta.rows_written ?? 0;
          return results;
        };
      }
      const value = Reflect.get(target, property, target) as unknown;
      return typeof value === "function" ? (value as (...a: unknown[]) => unknown).bind(target) : value;
    },
  });
}

async function newIpCount(): Promise<number> {
  const row = await env.DB.prepare("SELECT COALESCE(SUM(count), 0) AS n FROM rate_limits WHERE bucket LIKE '%:new-ips'").first<{ n: number }>();
  return row!.n;
}

/**
 * Runs a request and counts the rows it wrote. `rows` excludes the new-IP ceiling increment a first request from an
 * IP makes: the budget counts that once per new IP (the "+ 1" in fixedDailyWrites), not per request.
 */
async function measured(request: Request): Promise<{ response: Response; rows: number; allRows: number }> {
  const counter = { rows: 0 };
  const before = await newIpCount();
  const response = await handleRequest(request, { ...env, DB: countingDb(env.DB, counter) }, h.deps);
  const ceilingRows = (await newIpCount()) - before;
  return { response, rows: counter.rows - ceilingRows, allRows: counter.rows };
}

function rejectedSignIn(ip: string, device: { publicJwk: object }): Promise<Response> {
  h.ip = ip;
  return h.call("POST", `${API}/v1/auth/discord`, { body: { code: "not-a-real-code", codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk } });
}

type SharedBucket = "global" | "guest-global" | "new-ips";

function windowStart(route: RouteClass, bucket: SharedBucket): number {
  const { windowMs } = LIMITS[route][bucket === "new-ips" ? "newIps" : bucket === "guest-global" ? "guestGlobal" : bucket];
  return Math.floor(h.now / windowMs) * windowMs;
}

async function seed(route: RouteClass, bucket: SharedBucket, count: number): Promise<void> {
  await env.DB.prepare("INSERT INTO rate_limits (bucket, window_start, count) VALUES (?, ?, ?) ON CONFLICT (bucket, window_start) DO UPDATE SET count = excluded.count").bind(`${route}:${bucket}`, windowStart(route, bucket), count).run();
}

async function counterValue(route: RouteClass, bucket: SharedBucket): Promise<number> {
  const row = await env.DB.prepare("SELECT count FROM rate_limits WHERE bucket = ? AND window_start = ?").bind(`${route}:${bucket}`, windowStart(route, bucket)).first<{ count: number }>();
  return row?.count ?? 0;
}

async function inviteOnlySession(discordId: string): Promise<Record<string, string>> {
  const { body } = await h.signIn(discordId, await newDevice());
  return { Authorization: `Bearer ${body["accessToken"] as string}` };
}

describe("rate limits", () => {
  it("limit sign-in per IP, with Retry-After, without affecting other IPs", async () => {
    const device = await newDevice();
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) expect((await rejectedSignIn("198.51.100.1", device)).status).toBe(400);
    const limited = await rejectedSignIn("198.51.100.1", device);
    expect(limited.status).toBe(429);
    expect(await limited.json()).toEqual({ schemaVersion: 1, error: "rate_limited" });
    expect(Number(limited.headers.get("Retry-After"))).toBeGreaterThan(0);
    expect((await rejectedSignIn("198.51.100.2", device)).status).toBe(400);
  });

  it("never charge the global counters for requests that fail their credential check", async () => {
    const device = await newDevice();
    // Junk from several IPs: codes Discord rejects, refresh tokens that do not exist, invite codes never issued.
    for (let ip = 0; ip < 4; ip++) for (let i = 0; i < LIMITS.auth.ip.cap; i++) expect((await rejectedSignIn(`10.1.${ip}.1`, device)).status).toBe(400);
    for (let i = 0; i < 5; i++) expect((await h.refresh(`rt_${String(i).repeat(43)}`, device)).status).toBe(401);
    for (let account = 0; account < 3; account++) {
      h.ip = `198.18.0.${account}`;
      const auth = await inviteOnlySession(`17222222222222222${account}`);
      for (let i = 0; i < LIMITS.redeem.failures.cap; i++) {
        h.ip = `192.0.2.${account * 10 + i}`;
        expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: auth })).status).toBe(400);
      }
    }
    expect(await counterValue("refresh", "global")).toBe(0);
    expect(await counterValue("redeem", "global")).toBe(0);
    expect(await counterValue("redeem", "guest-global")).toBe(0);
    // Only the three real sign-ins above moved a sign-in counter: the guest one, as the accounts have no access (TM-07).
    expect(await counterValue("auth", "guest-global")).toBe(3);
    expect(await counterValue("auth", "global")).toBe(0);
  });

  it("let a real sign-in through after any amount of junk has used up the junk's own limits", async () => {
    const device = await newDevice();
    for (let ip = 0; ip < Math.ceil(40 / LIMITS.auth.ip.cap); ip++) {
      for (let i = 0; i <= LIMITS.auth.ip.cap; i++) await rejectedSignIn(`10.2.${ip}.1`, device);
    }
    h.ip = "203.0.113.50";
    expect((await h.signIn("173333333333333333", await newDevice())).status).toBe(200);
  });

  it("cap the requests that passed their credential check globally", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    await seed("auth", "global", LIMITS.auth.global.cap - 1);
    expect((await h.signIn("174444444444444444", await newDevice())).status).toBe(200);
    h.ip = "203.0.113.51";
    const refused = await h.signIn("174444444444444445", await newDevice());
    expect(refused.status).toBe(429);
    expect(await rowCount("users")).toBe(1);
  });

  it("cap sign-ins by accounts without access on their own small guest cap (TM-07)", async () => {
    expect(LIMITS.auth.guestGlobal.cap).toBeLessThan(LIMITS.auth.global.cap);
    await seed("auth", "guest-global", LIMITS.auth.guestGlobal.cap - 1);
    expect((await h.signIn("174444444444444446", await newDevice())).status).toBe(200);
    h.ip = "203.0.113.52";
    const refused = await h.signIn("174444444444444447", await newDevice());
    expect(refused.status).toBe(429);
    expect(refused.body["error"]).toBe("rate_limited");
    expect(await rowCount("users")).toBe(1);
    expect(await counterValue("auth", "global")).toBe(0);
  });

  it("limit refresh per account only once the token is known", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    const { body } = await h.signIn("175555555555555555", device);
    const accountBuckets = async () => rowCount("rate_limits", "bucket LIKE 'refresh:account:%'");
    expect((await h.refresh(`rt_${"B".repeat(43)}`, device)).status).toBe(401);
    expect(await accountBuckets()).toBe(0);
    expect((await h.refresh(body["refreshToken"] as string, device)).status).toBe(200);
    expect(await accountBuckets()).toBe(1);
  });

  it("limit redemption per account", async () => {
    const { body } = await h.signIn("171111111111111111", await newDevice());
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    const statuses: number[] = [];
    for (let i = 0; i <= LIMITS.redeem.failures.cap; i++) {
      h.ip = `192.0.2.${i}`;
      statuses.push((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: auth })).status);
    }
    expect(statuses).toEqual([...Array<number>(LIMITS.redeem.failures.cap).fill(400), 429]);
  });

  it("start again in the next window", async () => {
    const device = await newDevice();
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) await rejectedSignIn("198.51.100.3", device);
    expect((await rejectedSignIn("198.51.100.3", device)).status).toBe(429);
    h.now += LIMITS.auth.ip.windowMs;
    expect((await rejectedSignIn("198.51.100.3", device)).status).toBe(400);
  });

  it("store no clear IP address or account id", async () => {
    await rejectedSignIn("198.51.100.77", await newDevice());
    const dump = JSON.stringify((await env.DB.prepare("SELECT * FROM rate_limits").all()).results);
    expect(dump).not.toContain("198.51.100.77");
    // The per-IP counter and the new-IP ceiling: a rejected code never reaches the account or global counters.
    expect(await rowCount("rate_limits")).toBe(2);
  });
});

describe("reserved write budget (budget exhaustion test)", () => {
  it("keeps the sensitive routes' worst case inside half of the D1 free tier's daily writes", () => {
    expect(SENSITIVE_WRITE_BUDGET).toBe(D1_DAILY_WRITE_LIMIT / 2);
    // More than twice the expected 50 users' worth of accounts can fail as often as they may on the same day.
    expect(accountsWithinBudget()).toBeGreaterThanOrEqual(2 * 50);
    expect(worstCaseDailyWrites(accountsWithinBudget())).toBeLessThanOrEqual(SENSITIVE_WRITE_BUDGET);
    expect(worstCaseDailyWrites(accountsWithinBudget() + 1)).toBeGreaterThan(SENSITIVE_WRITE_BUDGET);
  });

  it("sizes the caps far above 50 active users refreshing daily plus normal sign-ins", () => {
    const perDay = (route: RouteClass) => LIMITS[route].global.cap * Math.ceil(DAY_MS / LIMITS[route].global.windowMs);
    const users = 50;
    // Daily refreshes are 12 times below the cap.
    expect(perDay("refresh")).toBeGreaterThanOrEqual(12 * users);
    // About one IP per user per day: the new-IP ceiling leaves four times that.
    expect(LIMITS.refresh.newIps.cap).toBeGreaterThanOrEqual(4 * users);
    // One user refreshing hourly all day stays under the per-IP counter; two devices for 12 hours a day under the account's.
    expect(LIMITS.refresh.ip.cap).toBeGreaterThanOrEqual(24);
    expect(LIMITS.refresh.account.cap * Math.ceil(DAY_MS / LIMITS.refresh.account.windowMs)).toBeGreaterThanOrEqual(2 * 12 * 2);
    // Normal sign-ins: a new device or a reinstall now and then; a quarter of all users signing in on one day fits 19 times.
    expect(perDay("auth")).toBeGreaterThan(19 * (users / 4));
  });

  it("keep counting and refusing one IP however much traffic other IPs sent (fail closed)", async () => {
    const device = await newDevice();
    // More junk from other IPs than any shared ceiling would allow.
    for (let ip = 0; ip < 16; ip++) for (let i = 0; i < LIMITS.auth.ip.cap; i++) await rejectedSignIn(`10.3.${ip}.1`, device);
    await seed("auth", "global", LIMITS.auth.global.cap);
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) expect((await rejectedSignIn("198.51.100.30", device)).status).toBe(400);
    expect((await rejectedSignIn("198.51.100.30", device)).status).toBe(429);
  });

  it("keep counting, refusing and auditing one account's redemptions however busy redemption is", async () => {
    for (let account = 0; account < 8; account++) {
      h.ip = `198.18.1.${account}`;
      const auth = await inviteOnlySession(`17777777777777777${account}`);
      for (let i = 0; i < LIMITS.redeem.failures.cap; i++) {
        h.ip = `192.0.2.${(account * LIMITS.redeem.failures.cap + i) % 8}`;
        expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: auth })).status).toBe(400);
      }
      h.ip = "192.0.2.8";
      expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: auth })).status).toBe(429);
    }
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed'")).toBe(8 * LIMITS.redeem.failures.cap);
  });

  it("charge the global redemption counter only for a redeemable code", async () => {
    const created = (await (await h.admin("POST", "/api/invites", { count: 3, expiresInDays: 1 })).json()) as { invites: { id: string; code: string }[] };
    const [used, revoked, expiring] = created.invites as [{ id: string; code: string }, { id: string; code: string }, { id: string; code: string }];
    h.ip = "198.18.2.1";
    const first = await inviteOnlySession("178888888888888881");
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: used.code }, headers: first })).status).toBe(200);
    expect(await counterValue("redeem", "global")).toBe(1);
    expect((await h.admin("POST", `/api/invites/${revoked.id}/revoke`)).status).toBe(200);

    const other = await inviteOnlySession("178888888888888882");
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: used.code }, headers: other })).status).toBe(400);
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: revoked.code }, headers: other })).status).toBe(400);
    h.now += 2 * DAY_MS;
    const later = await inviteOnlySession("178888888888888883");
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: expiring.code }, headers: later })).status).toBe(400);
    expect(await counterValue("redeem", "global")).toBe(0);
    h.now -= 2 * DAY_MS;
    expect(await counterValue("redeem", "global")).toBe(1);
    const reasons = (await env.DB.prepare("SELECT after_json FROM audit_log WHERE action = 'invite.redeem_failed' ORDER BY seq").all<{ after_json: string }>()).results;
    expect(reasons.map((r) => JSON.parse(r.after_json) as unknown)).toEqual([{ reason: "used" }, { reason: "revoked" }, { reason: "expired" }]);
  });

  it("charge the global refresh counter only after expiry, proof, nonce and revocation checks pass", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    const { body } = await h.signIn("179999999999999991", device);
    const token = body["refreshToken"] as string;
    const nowSeconds = Math.floor(h.now / 1000);
    expect((await h.refresh(token, await newDevice())).status).toBe(401);
    expect((await h.refresh(token, device, { timestamp: nowSeconds - 3600 })).status).toBe(401);
    const replayed = await device.proof(token, nowSeconds);
    await env.DB.prepare("INSERT INTO proof_nonces (nonce, expires_at) VALUES (?, ?)").bind(replayed.nonce, h.now + 600_000).run();
    expect((await h.refresh(token, device, { nonce: replayed.nonce })).status).toBe(401);
    await env.DB.prepare("UPDATE token_families SET revoked_at = ?, revoked_reason = 'logout'").bind(h.now).run();
    expect((await h.refresh(token, device)).status).toBe(401);
    expect(await counterValue("refresh", "global")).toBe(0);

    const second = await h.signIn("179999999999999992", device);
    await env.DB.prepare("UPDATE refresh_tokens SET expires_at = ?").bind(h.now - 1).run();
    expect((await h.refresh(second.body["refreshToken"] as string, device)).status).toBe(401);
    const third = await h.signIn("179999999999999993", device);
    await env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ? WHERE discord_id = '179999999999999993'").bind(h.now).run();
    expect((await h.refresh(third.body["refreshToken"] as string, device)).status).toBe(403);
    expect(await counterValue("refresh", "global")).toBe(0);

    const fourth = await h.signIn("179999999999999994", device);
    expect((await h.refresh(fourth.body["refreshToken"] as string, device)).status).toBe(200);
    expect(await counterValue("refresh", "global")).toBe(1);
  });

  it("answer a revoked user or a revoked device at sign-in before charging the global counter", async () => {
    const device = await newDevice();
    const { body } = await h.signIn("170000000000000001", device);
    const lost = await h.signIn("170000000000000002", device);
    expect(await counterValue("auth", "guest-global")).toBe(2);
    await env.DB.prepare("UPDATE users SET status = 'revoked', revoked_at = ? WHERE id = ?").bind(h.now, (body["user"] as { id: string }).id).run();
    await env.DB.prepare("UPDATE devices SET revoked_at = ? WHERE id = ?").bind(h.now, (lost.body["device"] as { id: string }).id).run();
    expect((await h.signIn("170000000000000001", device)).status).toBe(403);
    expect((await h.signIn("170000000000000002", device)).body["error"]).toBe("device_revoked");
    expect(await counterValue("auth", "guest-global")).toBe(2);
    expect(await counterValue("auth", "global")).toBe(0);
  });

  it("counts every row junk and failing accounts can create: the bound covers attack traffic", () => {
    let fixed = 0;
    const families = DEVICE_LIMIT_MAX + LIMITS.auth.account.cap * (DAY_MS / LIMITS.auth.account.windowMs);
    let perAccount = families * REUSE_ROWS;
    for (const limit of Object.values(LIMITS)) {
      fixed += limit.newIps.cap * (DAY_MS / limit.newIps.windowMs) * (limit.ip.cap + 1);
      fixed += limit.global.cap * (DAY_MS / limit.global.windowMs) * limit.rowsPerRequest;
      fixed += limit.guestGlobal.cap * (DAY_MS / limit.guestGlobal.windowMs) * limit.rowsPerRequest;
      perAccount += (limit.failureScope === "family" ? families : 1) * limit.failures.cap * (DAY_MS / limit.failures.windowMs) * limit.accountFailedRows;
    }
    expect(fixedDailyWrites()).toBe(fixed);
    expect(perAccountDailyWrites()).toBe(perAccount);
    expect(worstCaseDailyWrites(7)).toBe(fixed + 7 * perAccount);
  });

  it("a full new-IP ceiling does not block a valid sign-in or refresh from a new IP, and junk from new IPs writes nothing", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    await seed("auth", "new-ips", LIMITS.auth.newIps.cap);
    await seed("refresh", "new-ips", LIMITS.refresh.newIps.cap);
    const device = await newDevice();
    h.ip = "10.6.0.1";
    const signIn = await h.signIn("176666666666666661", device);
    expect(signIn.status).toBe(200);
    h.ip = "10.6.0.2";
    expect((await h.refresh(signIn.body["refreshToken"] as string, device)).status).toBe(200);
    for (let i = 0; i < 5; i++) {
      const { response, allRows } = await measured(
        new Request(`${API}/v1/auth/discord`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "CF-Connecting-IP": `10.6.1.${i}` },
          body: JSON.stringify({ code: "x", codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk }),
        }),
      );
      expect(response.status).toBe(400);
      expect(allRows).toBe(0);
    }
    expect(await rowCount("rate_limits", "bucket LIKE '%:ip:%'")).toBe(0);
  });

  it("an IP at its cap is still refused, also with every ceiling full and also for a valid request", async () => {
    const device = await newDevice();
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) expect((await rejectedSignIn("198.51.100.41", device)).status).toBe(400);
    await seed("auth", "new-ips", LIMITS.auth.newIps.cap);
    expect((await rejectedSignIn("198.51.100.41", device)).status).toBe(429);
    h.ip = "198.51.100.41";
    const valid = await h.signIn("176666666666666662", await newDevice());
    expect(valid.status).toBe(429);
    expect(await rowCount("users")).toBe(0);
  });

  it("counts every failed attempt on the account's own counter and refuses at its cap", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    h.ip = "198.18.5.1";
    const { body } = await h.signIn("176666666666666665", device);
    const token = body["refreshToken"] as string;
    const statuses: number[] = [];
    for (let i = 0; i <= LIMITS.refresh.failures.cap; i++) {
      h.ip = `198.18.5.${10 + (i % 10)}`;
      statuses.push((await h.refresh(token, await newDevice())).status);
    }
    expect(statuses).toEqual([...Array<number>(LIMITS.refresh.failures.cap).fill(401), 429]);
    // At its failure cap the account is refused even with a valid proof, until the window ends; its quota of verified
    // refreshes was not used up by the failures.
    h.ip = "198.18.5.30";
    expect((await h.refresh(token, device)).status).toBe(429);
    expect(await rowCount("rate_limits", "bucket LIKE 'refresh:account:%'")).toBe(0);
    h.now += LIMITS.refresh.failures.windowMs;
    expect((await h.refresh(token, device)).status).toBe(200);
  });

  it("refuses an account at its failure cap before any invite lookup, even with a valid code, until the window ends", async () => {
    const created = (await (await h.admin("POST", "/api/invites", { expiresInDays: 3 })).json()) as { invites: { code: string }[] };
    h.ip = "198.18.9.1";
    const auth = await inviteOnlySession("176666666666666690");
    for (let i = 0; i < LIMITS.redeem.failures.cap; i++) {
      expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: { ...auth, "CF-Connecting-IP": `198.18.9.${10 + i}` } })).status).toBe(400);
    }
    const audits = await rowCount("audit_log", "action = 'invite.redeem_failed'");
    const valid = await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: created.invites[0]!.code }, headers: { ...auth, "CF-Connecting-IP": "198.18.9.100" } });
    expect(valid.status).toBe(429);
    expect(await rowCount("invites", "redeemed_at IS NOT NULL")).toBe(0);
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed'")).toBe(audits);
    h.now += LIMITS.redeem.failures.windowMs;
    h.ip = "198.18.9.2";
    const later = await inviteOnlySession("176666666666666690");
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: created.invites[0]!.code }, headers: { ...later, "CF-Connecting-IP": "198.18.9.101" } })).status).toBe(200);
  });

  it("scopes refresh failures to the token family: failures on one device never block a valid refresh on another", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const laptop = await newDevice();
    const desktop = await newDevice();
    h.ip = "198.18.10.1";
    const a = await h.signIn("176666666666666695", laptop);
    const b = await h.signIn("176666666666666695", desktop);
    for (let i = 0; i < LIMITS.refresh.failures.cap; i++) {
      h.ip = `198.18.10.${10 + i}`;
      expect((await h.refresh(a.body["refreshToken"] as string, await newDevice())).status).toBe(401);
    }
    h.ip = "198.18.10.50";
    // Family A is at its failure cap: refused, even with its own valid proof.
    expect((await h.refresh(a.body["refreshToken"] as string, laptop)).status).toBe(429);
    // Family B of the same account is not affected.
    expect((await h.refresh(b.body["refreshToken"] as string, desktop)).status).toBe(200);
  });

  it("keeps failed-redemption audits within the account's cap under concurrent requests", async () => {
    h.ip = "198.18.7.1";
    const auth = await inviteOnlySession("176666666666666680");
    const results = await Promise.all(
      Array.from({ length: 20 }, (_, i) => h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: { ...auth, "CF-Connecting-IP": `198.18.7.${10 + i}` } })),
    );
    const statuses = results.map((r) => r.status).sort();
    expect(statuses).toEqual([...Array<number>(LIMITS.redeem.failures.cap).fill(400), ...Array<number>(20 - LIMITS.redeem.failures.cap).fill(429)]);
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed'")).toBeLessThanOrEqual(LIMITS.redeem.failures.cap);
  });

  it("answers attempts on a revoked family without writing anything beyond the IP counter", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    h.ip = "198.18.8.1";
    const { body } = await h.signIn("176666666666666681", device);
    expect((await h.call("POST", `${API}/v1/auth/logout`, { headers: { Authorization: `Bearer ${body["accessToken"] as string}` } })).status).toBe(200);
    for (let i = 0; i < 3; i++) {
      const { response, rows } = await measured(
        new Request(`${API}/v1/auth/refresh`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "CF-Connecting-IP": "198.18.8.1" },
          body: JSON.stringify({ refreshToken: body["refreshToken"], proof: await device.proof(body["refreshToken"] as string, Math.floor(h.now / 1000)) }),
        }),
      );
      expect(response.status).toBe(401);
      expect(rows).toBe(1);
    }
    expect(await rowCount("rate_limits", "bucket LIKE 'refresh:account%'")).toBe(0);
  });

  it("always audits refresh-token reuse, once per family, however many failures came before", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    // Any shared count of failures, full: no route counter may stand between a reuse and its audit row.
    await env.DB.prepare("INSERT INTO rate_limits (bucket, window_start, count) VALUES ('refresh:failures', ?, 1000000)").bind(Math.floor(h.now / DAY_MS) * DAY_MS).run();
    for (let i = 0; i < 3; i++) {
      const device = await newDevice();
      h.ip = `198.18.6.${i}`;
      const { body } = await h.signIn(`17666666666666667${i}`, device);
      await h.refresh(body["refreshToken"] as string, device);
      h.now += 60_000;
      expect((await h.refresh(body["refreshToken"] as string, device)).status).toBe(401);
      expect((await h.refresh(body["refreshToken"] as string, device)).status).toBe(401);
    }
    expect(await rowCount("token_families", "revoked_reason = 'refresh_token_reused'")).toBe(3);
    expect(await rowCount("audit_log", "action = 'session.refresh_token_reused'")).toBe(3);
  });

  it("the D1 write budget stays bounded under a flood of junk from 10,000 IPs", { timeout: 300_000 }, async () => {
    const sessions: Record<string, string>[] = [];
    for (let i = 0; i < 10; i++) {
      h.ip = `198.18.4.${i}`;
      sessions.push(await inviteOnlySession(`17555555555555550${i}`));
    }
    const device = await newDevice();
    const counter = { rows: 0 };
    const floodEnv = { ...env, DB: countingDb(env.DB, counter) };
    const junk = (i: number): Request => {
      const ip = `10.${100 + Math.floor(i / 65536)}.${Math.floor(i / 256) % 256}.${i % 256}`;
      const headers = { "Content-Type": "application/json", "CF-Connecting-IP": ip };
      if (i % 5 < 2) {
        return new Request(`${API}/v1/auth/discord`, {
          method: "POST",
          headers,
          body: JSON.stringify({ code: `junk${i}`, codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk }),
        });
      }
      if (i % 5 < 4) {
        return new Request(`${API}/v1/auth/refresh`, {
          method: "POST",
          headers,
          body: JSON.stringify({ refreshToken: `rt_${encodeBase64Url(crypto.getRandomValues(new Uint8Array(32)))}`, proof: { timestamp: Math.floor(h.now / 1000), nonce: "n".repeat(22), signature: "A".repeat(86) } }),
        });
      }
      return new Request(`${API}/v1/invites/redeem`, { method: "POST", headers: { ...headers, ...sessions[Math.floor(i / 5) % 10]! }, body: JSON.stringify({ code: "ABCD-EFGH-JKMN-PQRS" }) });
    };
    const statuses = new Map<number, number>();
    const batchSize = 100;
    for (let start = 0; start < 10_000; start += batchSize) {
      const responses = await Promise.all(Array.from({ length: batchSize }, (_, k) => handleRequest(junk(start + k), floodEnv, h.deps)));
      for (const r of responses) {
        statuses.set(r.status, (statuses.get(r.status) ?? 0) + 1);
        await r.body?.cancel();
      }
    }
    // Junk is refused as junk; it never locks anyone out by itself.
    expect([...statuses.keys()].every((s) => s === 400 || s === 401 || s === 429)).toBe(true);
    // Everything it wrote fits the per-IP share of the bound plus the share of the 10 accounts that sent junk (no
    // verified work happened), and every failed redemption was audited: nothing was dropped.
    let junkBound = 10 * perAccountDailyWrites();
    for (const limit of Object.values(LIMITS)) junkBound += limit.newIps.cap * (limit.ip.cap + 1);
    console.log(JSON.stringify({ flood: { requests: 10_000, rowsWritten: counter.rows, junkBound, worstCase: worstCaseDailyWrites(10), statuses: Object.fromEntries(statuses) } }));
    expect(counter.rows).toBeLessThanOrEqual(junkBound);
    expect(worstCaseDailyWrites(10)).toBeLessThanOrEqual(SENSITIVE_WRITE_BUDGET);
    expect(await rowCount("audit_log", "action = 'invite.redeem_failed'")).toBe(10 * LIMITS.redeem.failures.cap);
    // After the flood, a real user from a new IP signs in and refreshes.
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    h.ip = "203.0.113.200";
    const real = await newDevice();
    const signIn = await h.signIn("175555555555555559", real);
    expect(signIn.status).toBe(200);
    expect((await h.refresh(signIn.body["refreshToken"] as string, real)).status).toBe(200);
  });

  it("counts an IPv6 client per /64", async () => {
    const device = await newDevice();
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) expect((await rejectedSignIn(`2001:db8:aa:bb::${(i + 1).toString(16)}`, device)).status).toBe(400);
    expect((await rejectedSignIn("2001:0db8:00aa:00bb:ffff:ffff:ffff:ffff", device)).status).toBe(429);
    expect((await rejectedSignIn("2001:db8:aa:bc::1", device)).status).toBe(400);
    expect(await counterValue("auth", "new-ips")).toBe(2);
  });

  it("derives the IP key: IPv4 as is, IPv6 per /64, IPv4-mapped as IPv4", () => {
    expect(ipPrincipal("203.0.113.7")).toBe("203.0.113.7");
    expect(ipPrincipal("2001:db8::1")).toBe("2001:db8:0:0::/64");
    expect(ipPrincipal("2001:0DB8:0000:0000:1:2:3:4")).toBe("2001:db8:0:0::/64");
    expect(ipPrincipal("2001:db8:1:2:3:4:5:6")).toBe("2001:db8:1:2::/64");
    expect(ipPrincipal("::1")).toBe("0:0:0:0::/64");
    expect(ipPrincipal("::ffff:198.51.100.9")).toBe("198.51.100.9");
    expect(ipPrincipal("fe80::1::2")).toBe("fe80::1::2");
    expect(ipPrincipal("1:2:3")).toBe("1:2:3");
  });

  it("writes nothing for a request that is over a limit", async () => {
    const device = await newDevice();
    for (let i = 0; i < LIMITS.auth.ip.cap; i++) await rejectedSignIn("198.51.100.9", device);
    for (let i = 0; i < 25; i++) {
      const { response, rows } = await measured(
        new Request(`${API}/v1/auth/discord`, {
          method: "POST",
          headers: { "Content-Type": "application/json", "CF-Connecting-IP": "198.51.100.9" },
          body: JSON.stringify({ code: "x", codeVerifier: "v".repeat(43), redirectUri: REDIRECT, devicePublicKeyJwk: device.publicJwk }),
        }),
      );
      expect(response.status).toBe(429);
      expect(rows).toBe(0);
    }
    const counters = await env.DB.prepare("SELECT MAX(count) AS max FROM rate_limits WHERE bucket LIKE 'auth:ip:%'").first<{ max: number }>();
    expect(counters!.max).toBe(LIMITS.auth.ip.cap);
  });

  it("an attacker who exhausts redemption cannot block sign-in or refresh", async () => {
    const { body } = await h.signIn("181111111111111111", await newDevice());
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    const invite = (await (await h.admin("POST", "/api/invites", {})).json()) as { invites: { code: string }[] };
    await seed("redeem", "global", LIMITS.redeem.global.cap);
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: invite.invites[0]!.code }, headers: auth })).status).toBe(429);
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();
    const signIn = await h.signIn("191111111111111111", device);
    expect(signIn.status).toBe(200);
    expect((await h.refresh(signIn.body["refreshToken"] as string, device)).status).toBe(200);
  });

  it("throwaway accounts without access cannot use up sign-in, refresh or redemption for the people the owner let in (TM-07)", async () => {
    // A member: signed in and redeemed an invite.
    const memberDevice = await newDevice();
    const member = await h.signIn("182222222222222221", memberDevice);
    const invites = (await (await h.admin("POST", "/api/invites", { count: 2 })).json()) as { invites: { code: string }[] };
    const memberAuth = { Authorization: `Bearer ${member.body["accessToken"] as string}` };
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: invites.invites[0]!.code }, headers: memberAuth })).status).toBe(200);
    // A newly invited person signs in (no access yet) before the attack fills the guest cap.
    h.ip = "203.0.113.60";
    const inviteeDevice = await newDevice();
    const invitee = await h.signIn("182222222222222222", inviteeDevice);
    expect(invitee.body["access"]).toBe("invite_required");

    // Throwaway accounts use up every guest cap: their sign-ins and refreshes are refused from then on.
    await seed("auth", "guest-global", LIMITS.auth.guestGlobal.cap);
    await seed("refresh", "guest-global", LIMITS.refresh.guestGlobal.cap);
    await seed("redeem", "guest-global", LIMITS.redeem.guestGlobal.cap);
    h.ip = "203.0.113.61";
    expect((await h.signIn("182222222222222223", await newDevice())).status).toBe(429);
    expect((await h.refresh(invitee.body["refreshToken"] as string, inviteeDevice)).status).toBe(429);

    // The member still signs in and refreshes, and the invitee still redeems the code the owner gave them.
    h.ip = "203.0.113.62";
    const again = await h.signIn("182222222222222221", memberDevice);
    expect(again.status).toBe(200);
    expect(again.body["access"]).toBe("granted");
    expect((await h.refresh(again.body["refreshToken"] as string, memberDevice)).status).toBe(200);
    const inviteeAuth = { Authorization: `Bearer ${invitee.body["accessToken"] as string}` };
    const redeemed = await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: invites.invites[1]!.code }, headers: inviteeAuth });
    expect(redeemed.status).toBe(200);
    expect(await counterValue("auth", "global")).toBe(1);
    expect(await counterValue("refresh", "global")).toBe(1);
    expect(await counterValue("redeem", "global")).toBe(2);
  });

  it("counts a public-mode grant by redemption on the guest cap, never on invite redemptions (TM-07)", async () => {
    const { body } = await h.signIn("183333333333333331", await newDevice());
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    expect((await h.call("POST", `${API}/v1/invites/redeem`, { body: { code: "ABCD-EFGH-JKMN-PQRS" }, headers: auth })).status).toBe(200);
    expect(await counterValue("redeem", "guest-global")).toBe(1);
    expect(await counterValue("redeem", "global")).toBe(0);
  });

  it("each request writes no more rows than the budget assumes", async () => {
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const device = await newDevice();

    const verifier = "w".repeat(43);
    const signInRequest = (code: string, jwk: object) =>
      new Request(`${API}/v1/auth/discord`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip },
        body: JSON.stringify({ code, codeVerifier: verifier, redirectUri: REDIRECT, devicePublicKeyJwk: jwk }),
      });
    const rejected = await measured(signInRequest("not-issued", device.publicJwk));
    expect(rejected.response.status).toBe(400);
    const first = await measured(signInRequest(h.discordCode({ id: "201111111111111111" }, verifier), device.publicJwk));
    expect(first.response.status).toBe(200);
    const again = await measured(signInRequest(h.discordCode({ id: "201111111111111111" }, verifier), device.publicJwk));
    expect(again.response.status).toBe(200);
    const tokens = (await again.response.json()) as Record<string, string>;

    const refreshRequest = async (refreshToken: string) =>
      new Request(`${API}/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip },
        body: JSON.stringify({ refreshToken, proof: await device.proof(refreshToken, Math.floor(h.now / 1000)) }),
      });
    const unknownToken = await measured(await refreshRequest(`rt_${"C".repeat(43)}`));
    expect(unknownToken.response.status).toBe(401);
    const badProof = await measured(
      new Request(`${API}/v1/auth/refresh`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip },
        body: JSON.stringify({ refreshToken: tokens["refreshToken"], proof: await (await newDevice()).proof(tokens["refreshToken"]!, Math.floor(h.now / 1000)) }),
      }),
    );
    expect(badProof.response.status).toBe(401);
    const refreshed = await measured(await refreshRequest(tokens["refreshToken"]!));
    expect(refreshed.response.status).toBe(200);
    // Presenting the rotated token again, after any retry window: reuse, the family is revoked and audited.
    h.now += 60_000;
    const reused = await measured(await refreshRequest(tokens["refreshToken"]!));
    expect(reused.response.status).toBe(401);

    await h.admin("PUT", "/api/settings", { accessMode: "invite-only" });
    const invited = await h.signIn("211111111111111111", await newDevice());
    const created = (await (await h.admin("POST", "/api/invites", { count: 2 })).json()) as { invites: { code: string }[] };
    const redeemRequest = (code: string, accessToken = invited.body["accessToken"] as string) =>
      new Request(`${API}/v1/invites/redeem`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "CF-Connecting-IP": h.ip, Authorization: `Bearer ${accessToken}` },
        body: JSON.stringify({ code }),
      });
    const unknownCode = await measured(redeemRequest("ABCD-EFGH-JKMN-PQRS"));
    expect(unknownCode.response.status).toBe(400);
    const redeemed = await measured(redeemRequest(created.invites[0]!.code));
    expect(redeemed.response.status).toBe(200);
    const other = await h.signIn("212111111111111111", await newDevice());
    const usedCode = await measured(redeemRequest(created.invites[0]!.code, other.body["accessToken"] as string));
    expect(usedCode.response.status).toBe(400);

    console.log(
      JSON.stringify({
        measuredRowsWritten: {
          signInRejected: rejected.rows,
          signInNewDevice: first.rows,
          signInKnownDevice: again.rows,
          refreshUnknownToken: unknownToken.rows,
          refreshBadProof: badProof.rows,
          refresh: refreshed.rows,
          refreshReused: reused.rows,
          redeemUnknownCode: unknownCode.rows,
          redeemOk: redeemed.rows,
          redeemUsedCode: usedCode.rows,
        },
      }),
    );
    // `rows` leaves out the new-IP ceiling increment, which the bound counts once per counted IP.
    // A request failing before its account is known writes its IP counter only. A failed attempt by a known account
    // adds accountFailedRows; refresh-token reuse adds its one-off REUSE_ROWS.
    expect(rejected.rows).toBeLessThanOrEqual(1);
    expect(unknownToken.rows).toBeLessThanOrEqual(1);
    expect(badProof.rows).toBeLessThanOrEqual(1 + LIMITS.refresh.accountFailedRows);
    expect(reused.rows).toBeLessThanOrEqual(1 + LIMITS.refresh.accountFailedRows + REUSE_ROWS);
    expect(unknownCode.rows).toBeLessThanOrEqual(1 + LIMITS.redeem.accountFailedRows);
    expect(usedCode.rows).toBeLessThanOrEqual(1 + LIMITS.redeem.accountFailedRows);
    // Verified requests, counters included.
    expect(first.rows).toBeLessThanOrEqual(LIMITS.auth.rowsPerRequest);
    expect(again.rows).toBeLessThanOrEqual(LIMITS.auth.rowsPerRequest);
    expect(refreshed.rows).toBeLessThanOrEqual(LIMITS.refresh.rowsPerRequest);
    expect(redeemed.rows).toBeLessThanOrEqual(LIMITS.redeem.rowsPerRequest);
  });
});

describe("upload limits sized for a whole pack (FR-ONL-14; PLAN_V2_S5B §2.6, ADR-0015 addendum 3)", () => {
  it("allows 300 uploads and 400 attempts per account a day, 2 000 and 4 000 for everyone, inside the D1 write budget", async () => {
    expect(ACCOUNT_LIMITS.upload.account).toEqual({ cap: 300, windowMs: DAY_MS });
    expect(ACCOUNT_LIMITS["upload-attempt"].account).toEqual({ cap: 400, windowMs: DAY_MS });
    expect(ACCOUNT_LIMITS.upload.global).toEqual({ cap: 2000, windowMs: DAY_MS });
    expect(ACCOUNT_LIMITS["upload-attempt"].global).toEqual({ cap: 4000, windowMs: DAY_MS });
    // The 300-file backstop fits in one day's uploads, so a whole pack is saved the day it is imported.
    expect(ACCOUNT_LIMITS.upload.account.cap).toBeGreaterThanOrEqual(DEFAULT_SETTINGS.uploadLimitFiles);

    // The rows one accepted upload really writes (meta.rows_written: counters, ledger, owner index, upload row and its
    // indexes, audit), measured here and bounded by rowsPerRequest, which the daily budget uses.
    await h.admin("PUT", "/api/settings", { accessMode: "public" });
    const { body } = await h.signIn("213000000000000001", await newDevice());
    const auth = { Authorization: `Bearer ${body["accessToken"] as string}` };
    const send = (bytes: Uint8Array) => {
      const data = new FormData();
      data.append("file", new File([bytes], "image.png", { type: "image/png" }));
      return new Request(`${API}/v1/uploads`, { method: "POST", headers: { ...auth, "CF-Connecting-IP": h.ip }, body: data });
    };
    const rows: number[] = [];
    for (let i = 0; i < 3; i++) {
      const upload = await measured(send(png({ idatBytes: 100 + i })));
      expect(upload.response.status).toBe(201);
      rows.push(upload.rows);
    }
    const again = await measured(send(png({ idatBytes: 100 })));
    expect(again.response.status).toBe(200);
    console.log(JSON.stringify({ measuredRowsWritten: { upload: rows, uploadSameBytes: again.rows } }));
    for (const n of rows) expect(n).toBeLessThanOrEqual(ACCOUNT_LIMITS.upload.rowsPerRequest);
    expect(again.rows).toBeLessThanOrEqual(ACCOUNT_LIMITS["upload-attempt"].rowsPerRequest);

    // With 50 accounts failing as often as they may, the account routes stay inside the half of the free tier's daily
    // writes that the sensitive routes leave.
    expect(accountRouteDailyWrites(50)).toBeLessThan(D1_DAILY_WRITE_LIMIT - SENSITIVE_WRITE_BUDGET);
  });
});
