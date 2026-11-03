// The Cloudflare REST API calls shared by scripts/provision.mjs (deploy workflow) and scripts/backup.mjs (backup
// workflow). The same endpoints wrangler 4 uses. Credentials come from CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID
// in the environment (GitHub environment secrets) and are never printed.

export function credentials() {
  const token = process.env.CLOUDFLARE_API_TOKEN;
  const account = process.env.CLOUDFLARE_ACCOUNT_ID;
  if (!token || !account) throw new Error("CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID must be set (GitHub environment secrets)");
  if (!/^[0-9a-f]{32}$/.test(account)) throw new Error("CLOUDFLARE_ACCOUNT_ID is not a 32-character account ID (Cloudflare dashboard → Account home → the ⋯ menu next to the account → Copy account ID)");
  return { token, account };
}

export async function cf(method, path, body) {
  const { token, account } = credentials();
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  let json = null;
  if (text.length > 0) {
    try {
      json = JSON.parse(text);
    } catch {
      json = { success: false, errors: [{ code: 0, message: `non-JSON response (HTTP ${response.status})` }] };
    }
  }
  return { status: response.status, ok: response.ok && json?.success !== false, result: json?.result, errors: json?.errors ?? [] };
}

export function describe(r) {
  const errors = r.errors.map((e) => `${e.code}: ${e.message}`).join("; ");
  return `HTTP ${r.status}${errors ? ` (${errors})` : ""}`;
}

export function must(r, what) {
  if (!r.ok) throw new Error(`${what} failed: ${describe(r)}`);
  return r.result;
}

export const hasCode = (r, code) => r.errors.some((e) => e.code === code);

/** Every D1 database whose name Cloudflare matches to `nameFilter`, as listed ({ uuid, name, created_at, ... }). */
export async function listDatabases(nameFilter) {
  const all = [];
  for (let page = 1; page <= 50; page++) {
    const list = must(await cf("GET", `/d1/database?name=${encodeURIComponent(nameFilter)}&per_page=100&page=${page}`), "Listing D1 databases");
    all.push(...list);
    if (list.length < 100) return all;
  }
  throw new Error("too many D1 databases to list");
}

/** The UUID of the D1 database with exactly this name, or null when there is none. */
export async function findDatabase(name) {
  for (let page = 1; page <= 50; page++) {
    const list = must(await cf("GET", `/d1/database?name=${encodeURIComponent(name)}&per_page=100&page=${page}`), "Listing D1 databases");
    const match = list.filter((d) => d.name === name);
    if (match.length > 1) throw new Error(`more than one D1 database is called ${name}`);
    if (match.length === 1) return match[0].uuid;
    if (list.length < 100) return null;
  }
  throw new Error("too many D1 databases to list");
}
