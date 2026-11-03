// Rate limits for the sensitive routes only (NFR-SVC-03, ADR-0011): sign-in, refresh and invite redemption.
//
// Layers, in order:
// 1. Edge: Cloudflare WAF rate-limiting rules per IP over the whole API host (docs/owner/cloudflare-setup.md §10).
//    Unauthenticated floods are the edge's job.
// 2. Per IP (`ip`, IPv6 counted per /64), before anything else. An IP that already has a counter in the current window
//    is counted and refused (429) at its cap. A new IP gets a counter only while the route's shared new-IP ceiling has
//    room; once it is full, new IPs are not counted but are not refused either: the request goes on to its credential
//    check, which reads only. So junk can never lock legitimate users out, and it can never create unbounded rows.
//    Why leaving overflow IPs uncounted is safe (threat model): no credential here can be brute-forced through the
//    service. Refresh tokens are 256-bit random values; OAuth codes are validated by Discord, not by us; redemption
//    needs an authenticated account and is limited per account. Floods are shed by the edge WAF.
// 3. Per account (`checkAccount`, `failure`, `verified`): as soon as the account is known (sign-in: Discord accepted
//    the code; refresh: the token row exists; redemption: the session's account) an account at its cap is refused
//    (read only), and so is a failure scope at its failure cap: the account itself for sign-in and redemption, the
//    token family for refresh (failures on one family never block a valid refresh on another). Every failed attempt
//    is counted on that scope's failure counter (`failure`), separate from the counter of verified requests so
//    failures never use up a legitimate quota; nothing shared can switch that off. A failed redemption is counted before its audit row
//    is written, so those rows are bounded per account even under concurrency; the refresh-token reuse audit is always
//    written (at most once per family). Attempts on a revoked family write nothing beyond the per-IP counter.
// 4. Global (`verified`): the account and global counters are counted together only after the full credential check
//    passed: for sign-in, the account, its device and its Discord id are not revoked or banned; for refresh, the token
//    is unexpired and unused (or a valid retry), the proof and nonce are good and the user, family and device are
//    live; for redemption, the code is redeemable or public mode grants access. Junk never touches a global counter.
//    Two global pools (threat-model review 2026-10-25, TM-07). `global` is shared by the people the owner let in:
//    accounts with access, and redemptions of an invite code the owner issued. `guestGlobal` is a smaller cap for
//    everyone else: sign-ins and refreshes by accounts without access (a new Discord account the owner never invited,
//    or an invited person who has not redeemed yet), and public-mode grants by redemption. So throwaway Discord accounts
//    can use up only the guest cap; existing users' sign-ins and daily refreshes, and invite redemptions, are never
//    refused because of them. What the guest cap can still delay is the first sign-in of a newly invited person on a
//    day with such an attack; the owner can ban the accounts (each is listed in the panel).
//
// Write budget. Every counter stops at its cap and a request over a cap writes nothing.
// - Per-IP rows: per window at most `newIps.cap` IPs are counted, each at most `ip.cap` times.
// - Verified work: bounded by the global cap, each request writing at most `rowsPerRequest` rows.
// - Failed attempts by known accounts: bounded per account (`perAccountDailyWrites()`): account counter rows, failed
//   redemption audits, and refresh-token reuse (revocation and audit, once per family; an account has at most
//   DEVICE_LIMIT_MAX live families plus the ones its capped sign-ins create that day).
// `worstCaseDailyWrites(accounts)` adds the three for a given number of accounts attacking at once, and a test keeps
// it inside `SENSITIVE_WRITE_BUDGET` for `accountsWithinBudget()` accounts (several times the expected user base).
// Accounts exist only through verified sign-ins and are each visible to the owner and removable with a ban.
//
// Sizing (re-derived 2026-10-24) for 50 active users: refreshing daily is 50 refreshes a day against a global refresh
// cap of 600 (12 times); normal sign-ins (a new device, a reinstall) are a handful a day against 240.
//
// IPs and account ids are stored only as a keyed hash that changes every day, and counters are deleted after 24 hours.

import { HttpError } from "./http.ts";
import { DEVICE_LIMIT_MAX } from "./db/settings.ts";
import { hmacSha256Hex } from "./lib/crypto.ts";

export type RouteClass = "auth" | "refresh" | "redeem";
/**
 * Which global cap a verified request is counted on (TM-07): `member` (the shared `global`) for accounts with access and
 * invite redemptions, `guest` (`guestGlobal`) for accounts without access and public-mode grants.
 */
export type GlobalPool = "member" | "guest";
/**
 * Routes that need a valid session before anything is counted (user uploads, V2-S5). They have no per-IP layer: a
 * request without a valid session only reads (`requireSession`), so junk writes nothing. Their writes are bounded on
 * their own (`accountRouteDailyWrites`), outside the sensitive routes' budget below.
 */
export type AccountRoute = "upload" | "upload-attempt" | "roster-report" | "study-label";

export interface Bucket {
  cap: number;
  windowMs: number;
}

/** The counters every limited route has once its account is known. */
export interface CountedLimit {
  /** Per account: requests that passed the full credential check (counted with the global counter). */
  account: Bucket;
  /**
   * Failed attempts, on a counter of their own so failures never use up the quota above, scoped by `failureScope`:
   * per account for sign-in and redemption (the caller is that account), per token family for refresh (a valid proof
   * on another family of the same account is never blocked by failures on this one).
   */
  failures: Bucket;
  failureScope: "account" | "family";
  /** Every request of this class that passed its full credential check, all clients together. */
  global: Bucket;
  /**
   * Most D1 rows one failed attempt by a known account writes: its account counter, plus the failure audit for
   * redemption (refresh-token reuse is counted separately, once per family). Measured by test/ratelimit.test.ts.
   */
  accountFailedRows: number;
  /**
   * Most D1 rows one request that passed its credential check writes, every counter and index row included (D1 bills
   * index rows). Measured by test/ratelimit.test.ts, which fails if a change makes a request write more.
   */
  rowsPerRequest: number;
}

export interface Limit extends CountedLimit {
  /**
   * Verified requests of the `guest` pool, all such clients together (TM-07): sign-ins and refreshes by accounts
   * without access, and public-mode grants by redemption. Kept small and apart from `global`, which they never touch.
   */
  guestGlobal: Bucket;
  /** Per client IP (IPv6: per /64), before anything else. */
  ip: Bucket;
  /** New IPs given a counter per window, all clients together; same window as `ip`. When full, new IPs go uncounted. */
  newIps: Bucket;
}

const HOUR_MS = 60 * 60 * 1000;
export const DAY_MS = 24 * HOUR_MS;

export const LIMITS: Readonly<Record<RouteClass, Limit>> = {
  auth: {
    ip: { cap: 20, windowMs: DAY_MS },
    newIps: { cap: 100, windowMs: DAY_MS },
    account: { cap: 10, windowMs: DAY_MS },
    failures: { cap: 10, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 240, windowMs: DAY_MS },
    guestGlobal: { cap: 60, windowMs: DAY_MS },
    accountFailedRows: 1,
    rowsPerRequest: 18,
  },
  refresh: {
    ip: { cap: 24, windowMs: DAY_MS },
    newIps: { cap: 200, windowMs: DAY_MS },
    account: { cap: 48, windowMs: DAY_MS },
    failures: { cap: 5, windowMs: DAY_MS },
    failureScope: "family",
    global: { cap: 600, windowMs: DAY_MS },
    guestGlobal: { cap: 60, windowMs: DAY_MS },
    accountFailedRows: 1,
    rowsPerRequest: 14,
  },
  redeem: {
    ip: { cap: 20, windowMs: DAY_MS },
    newIps: { cap: 50, windowMs: DAY_MS },
    account: { cap: 10, windowMs: DAY_MS },
    failures: { cap: 10, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 100, windowMs: DAY_MS },
    guestGlobal: { cap: 50, windowMs: DAY_MS },
    accountFailedRows: 4,
    rowsPerRequest: 10,
  },
};

/**
 * Uploads (FR-ONL-09, FR-ONL-14; ADR-0015 addendum 3): sized so a whole pack of own pictures (about 100, up to the
 * 300-file backstop) is saved in one day. An account may make 300 verified uploads a day; refused files (422) count
 * as failures. All accounts together: 2 000 uploads a day. One upload writes at most `rowsPerRequest` rows (measured
 * by test/ratelimit.test.ts; 13 in the S5b-0 spike before the ledger owner index), so uploads and attempts stay inside
 * the half of the D1 free tier's daily writes that the sensitive routes do not reserve.
 */
export const ACCOUNT_LIMITS: Readonly<Record<AccountRoute, CountedLimit>> = {
  upload: {
    account: { cap: 300, windowMs: DAY_MS },
    failures: { cap: 10, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 2000, windowMs: DAY_MS },
    accountFailedRows: 1,
    rowsPerRequest: 16,
  },
  // Every upload request with a valid session, counted before the body is read or parsed (security review 2026-10-24,
  // L1): this also bounds the requests that end in the same-bytes answer, the limit (409) or a refused body (400, 415).
  // It has no failure counter of its own (failures are the "upload" route's). 400 a day per account: a whole pack plus
  // its retries.
  "upload-attempt": {
    account: { cap: 400, windowMs: DAY_MS },
    failures: { cap: 0, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 4000, windowMs: DAY_MS },
    accountFailedRows: 0,
    rowsPerRequest: 2,
  },
  // Character sightings from the owner's app (D35, src/routes/roster.ts): only accounts the owner marked (at most 2) pass
  // the credential check; the app sends a batch of at most 4 names, at most once every 30 minutes, and only while it
  // has unknown names to report. A request that adds characters also writes the roster rows, the new version and the
  // audit row (measured by test/roster.test.ts). Refused reports (another account, a bad body) count as failures.
  "roster-report": {
    account: { cap: 30, windowMs: DAY_MS },
    failures: { cap: 5, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 60, windowMs: DAY_MS },
    accountFailedRows: 1,
    rowsPerRequest: 40,
  },
  // Automatic labelling of the owner's study recordings (D37, src/routes/study.ts): only accounts the owner marked pass
  // the credential check. An hour of play is about 10 scene requests and one facts request per match, so 300 a day is
  // several long sittings with retries; the cap also bounds what a stolen owner session could spend. A forwarded request
  // writes its counter row and an audit row (with its index and sequence rows). Refused requests count as failures.
  "study-label": {
    account: { cap: 300, windowMs: DAY_MS },
    failures: { cap: 20, windowMs: DAY_MS },
    failureScope: "account",
    global: { cap: 600, windowMs: DAY_MS },
    accountFailedRows: 1,
    rowsPerRequest: 6,
  },
};

function isAccountRoute(route: RouteClass | AccountRoute): route is AccountRoute {
  return Object.hasOwn(ACCOUNT_LIMITS, route);
}

function limitOf(route: RouteClass | AccountRoute): CountedLimit {
  return isAccountRoute(route) ? ACCOUNT_LIMITS[route] : LIMITS[route];
}

/** The guest cap of a sensitive route; the account routes need a session with access, so they have no guests. */
function guestLimitOf(route: RouteClass | AccountRoute): Bucket {
  if (isAccountRoute(route)) throw new Error(`${route} has no guest pool`);
  return LIMITS[route].guestGlobal;
}

/** The most rows the account routes can write in one day with `accounts` accounts failing as often as they may. */
export function accountRouteDailyWrites(accounts: number): number {
  let total = 0;
  for (const limit of Object.values(ACCOUNT_LIMITS)) total += perDay(limit.global) * limit.rowsPerRequest + accounts * perDay(limit.failures) * limit.accountFailedRows;
  return total;
}

/** Rows of one refresh-token reuse: the family revocation and its audit row (with its index and sequence rows). */
export const REUSE_ROWS = 4;

/** D1 free tier: rows written per day (ADR-0010, VERIFIED 2026-10-23). */
export const D1_DAILY_WRITE_LIMIT = 100_000;
/** At most this share of the daily writes may ever go to the sensitive routes and their counters. */
export const SENSITIVE_WRITE_BUDGET = D1_DAILY_WRITE_LIMIT / 2;

function perDay(bucket: Bucket): number {
  return bucket.cap * Math.ceil(DAY_MS / bucket.windowMs);
}

/** Rows not tied to any account: counted IPs and verified work. Bounded whatever the number of IPs sending junk. */
export function fixedDailyWrites(limits: Readonly<Record<RouteClass, Limit>> = LIMITS): number {
  let total = 0;
  for (const limit of Object.values(limits)) {
    if (limit.newIps.windowMs !== limit.ip.windowMs) throw new Error("newIps and ip must share a window");
    // Per-IP counters: at most newIps.cap IPs per window, each counted at most ip.cap times, plus one ceiling
    // increment per counted IP.
    total += perDay(limit.newIps) * (limit.ip.cap + 1);
    // Verified work: bounded by the two global caps (a request refused earlier writes nothing more).
    total += (perDay(limit.global) + perDay(limit.guestGlobal)) * limit.rowsPerRequest;
  }
  return total;
}

/** The most rows one account's failed attempts can write in one day, refresh-token reuse included. */
export function perAccountDailyWrites(limits: Readonly<Record<RouteClass, Limit>> = LIMITS): number {
  let total = 0;
  // An account has at most DEVICE_LIMIT_MAX live families plus those its capped sign-ins create today.
  const families = DEVICE_LIMIT_MAX + perDay(limits.auth.account);
  for (const limit of Object.values(limits)) total += (limit.failureScope === "family" ? families : 1) * perDay(limit.failures) * limit.accountFailedRows;
  // Each family is revoked for reuse at most once.
  total += families * REUSE_ROWS;
  return total;
}

/** The most rows the sensitive routes can write in one day with `accounts` accounts failing as often as they may. */
export function worstCaseDailyWrites(accounts: number, limits: Readonly<Record<RouteClass, Limit>> = LIMITS): number {
  return fixedDailyWrites(limits) + accounts * perAccountDailyWrites(limits);
}

/** How many accounts can fail as often as they may in one day before the sensitive routes exceed their budget. */
export function accountsWithinBudget(limits: Readonly<Record<RouteClass, Limit>> = LIMITS): number {
  return Math.floor((SENSITIVE_WRITE_BUDGET - fixedDailyWrites(limits)) / perAccountDailyWrites(limits));
}

/**
 * The key for an IP: IPv4 as is, IPv6 reduced to its /64 (one subscriber usually holds a whole /64, so per-address
 * counting would hand one client 2^64 principals). IPv4-mapped IPv6 counts as its IPv4 address. Text that does not
 * parse is used as it is.
 */
export function ipPrincipal(ip: string): string {
  if (!ip.includes(":")) return ip;
  const halves = ip.toLowerCase().split("::");
  if (halves.length > 2) return ip;
  const parse = (part: string): string[] => (part === "" ? [] : part.split(":"));
  const head = parse(halves[0]!);
  const tail = halves.length === 2 ? parse(halves[1]!) : [];
  const all = [...head, ...tail];
  const last = all[all.length - 1];
  if (last !== undefined && last.includes(".")) {
    // ::ffff:a.b.c.d
    if (halves.length === 2 && head.length === 0 && tail.length === 2 && tail[0] === "ffff" && /^\d{1,3}(\.\d{1,3}){3}$/.test(last)) return last;
    return ip;
  }
  const missing = 8 - all.length;
  if ((halves.length === 2 && missing < 1) || (halves.length === 1 && missing !== 0)) return ip;
  const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill("0"), ...tail];
  if (groups.some((g) => !/^[0-9a-f]{1,4}$/.test(g))) return ip;
  return `${groups
    .slice(0, 4)
    .map((g) => g.replace(/^0+(?=.)/, ""))
    .join(":")}::/64`;
}

interface Counter {
  name: string;
  cap: number;
  windowStart: number;
  windowMs: number;
}

export class RateLimiter {
  private readonly db: D1Database;
  private readonly now: number;
  private readonly hashKey: Uint8Array;

  constructor(db: D1Database, now: number, hashKey: Uint8Array) {
    this.db = db;
    this.now = now;
    this.hashKey = hashKey;
  }

  private async keyed(kind: string, value: string): Promise<string> {
    const day = new Date(this.now).toISOString().slice(0, 10);
    const digest = await hmacSha256Hex(this.hashKey, `dbdp-ratelimit-v1|${kind}|${day}|${value}`);
    return digest.slice(0, 32);
  }

  private counter(name: string, bucket: Bucket): Counter {
    return { name, cap: bucket.cap, windowStart: Math.floor(this.now / bucket.windowMs) * bucket.windowMs, windowMs: bucket.windowMs };
  }

  private refuse(counter: Counter): never {
    const retryAfter = String(Math.max(1, Math.ceil((counter.windowStart + counter.windowMs - this.now) / 1000)));
    throw new HttpError(429, "rate_limited", { "Retry-After": retryAfter });
  }

  private async read(counters: Counter[]): Promise<Map<string, number>> {
    const match = counters.map(() => "(bucket = ? AND window_start = ?)").join(" OR ");
    const { results } = await this.db
      .prepare(`SELECT bucket, window_start, count FROM rate_limits WHERE ${match}`)
      .bind(...counters.flatMap((c) => [c.name, c.windowStart]))
      .all<{ bucket: string; window_start: number; count: number }>();
    return new Map(results.map((r) => [`${r.bucket}@${r.window_start}`, r.count]));
  }

  private upsert(counter: Counter): D1PreparedStatement {
    return this.db
      .prepare(
        "INSERT INTO rate_limits (bucket, window_start, count) VALUES (?, ?, 1) " +
          "ON CONFLICT (bucket, window_start) DO UPDATE SET count = count + 1 WHERE count < ? RETURNING count",
      )
      .bind(counter.name, counter.windowStart, counter.cap);
  }

  private value(counts: Map<string, number>, counter: Counter): number {
    return counts.get(`${counter.name}@${counter.windowStart}`) ?? 0;
  }

  /** Counts one request against every counter in one transaction, or throws 429 when one is at its cap. */
  private async increment(counters: Counter[]): Promise<void> {
    const outcome = await this.db.batch<{ count: number }>(counters.map((c) => this.upsert(c)));
    const refused = counters.find((_, i) => outcome[i]!.results.length === 0);
    if (refused) this.refuse(refused);
  }

  /** Room taken in a shared ceiling, on its own statement; false when it is full. */
  private async takeRoom(ceiling: Counter): Promise<boolean> {
    return (await this.upsert(ceiling).all<{ count: number }>()).results.length > 0;
  }

  private ipCounter(route: RouteClass, key: string): Counter {
    return this.counter(`${route}:ip:${key}`, LIMITS[route].ip);
  }

  private async accountCounter(route: RouteClass | AccountRoute, account: string): Promise<Counter> {
    return this.counter(`${route}:account:${await this.keyed("account", account)}`, limitOf(route).account);
  }

  /**
   * The per-IP counter, before anything else. A counted IP is refused (429, nothing written) at its cap. A new IP is
   * counted only while the new-IP ceiling has room; otherwise it goes on uncounted and is never refused for that.
   */
  async ip(route: RouteClass, ip: string): Promise<void> {
    const counter = this.ipCounter(route, await this.keyed("ip", ipPrincipal(ip)));
    const ceiling = this.counter(`${route}:new-ips`, LIMITS[route].newIps);
    const counts = await this.read([counter, ceiling]);
    if (counts.has(`${counter.name}@${counter.windowStart}`)) {
      if (this.value(counts, counter) >= counter.cap) this.refuse(counter);
      await this.increment([counter]);
      return;
    }
    if (this.value(counts, ceiling) >= ceiling.cap) return;
    if (await this.takeRoom(ceiling)) await this.increment([counter]);
  }

  /** The failure counter of `scope`: an account id, or a token family id for refresh. */
  private async failureCounter(route: RouteClass | AccountRoute, scope: string): Promise<Counter> {
    const kind = limitOf(route).failureScope;
    return this.counter(`${route}:${kind}-failures:${await this.keyed(kind, scope)}`, limitOf(route).failures);
  }

  /**
   * Refuses (429, read only, nothing counted) at the cap of the account's verified-request counter or of the failure
   * counter of `failureScope` (the account itself for sign-in and redemption, the token family for refresh). Called
   * as soon as the account is known, before any credential or invite lookup, so a scope that used up its failures
   * cannot try again (not even with a valid code or proof) until the window ends.
   */
  async checkAccount(route: RouteClass | AccountRoute, account: string, failureScope: string = account): Promise<void> {
    const counters = [await this.accountCounter(route, account), await this.failureCounter(route, failureScope)];
    const counts = await this.read(counters);
    const over = counters.find((c) => this.value(counts, c) >= c.cap);
    if (over) this.refuse(over);
  }

  /**
   * After the full credential check passed: the account's counter (when given) and the route's global counter of
   * `pool` (`global`, or `guestGlobal` for the sensitive routes' guests; TM-07), read first and refused (429, nothing
   * written) at either cap, then counted together.
   */
  async verified(route: RouteClass | AccountRoute, account?: string, pool: GlobalPool = "member"): Promise<void> {
    const counters = [pool === "member" ? this.counter(`${route}:global`, limitOf(route).global) : this.counter(`${route}:guest-global`, guestLimitOf(route))];
    if (account !== undefined) counters.push(await this.accountCounter(route, account));
    const counts = await this.read(counters);
    const over = counters.find((c) => this.value(counts, c) >= c.cap);
    if (over) this.refuse(over);
    await this.increment(counters);
  }

  /**
   * A failed attempt, counted atomically (conditional upsert) on the failure counter of its scope (account, or token
   * family for refresh), which is separate from the account's counter of verified requests. Throws 429 without writing when it is at its cap, so a
   * caller that records the failure (an audit row) does so only after this succeeded.
   */
  async failure(route: RouteClass | AccountRoute, scope: string): Promise<void> {
    await this.increment([await this.failureCounter(route, scope)]);
  }
}
