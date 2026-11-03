// Screenshot evidence for the Owner Panel (docs/evidence/panel/): every section in dark and light, plus the new-code,
// confirmation, error, staging and narrow-width states. Runs Chromium through playwright-core against
// scripts/mock-server.mjs, which serves dist/ with the real CSP and sample data. The production and staging host names
// are mapped to 127.0.0.1 inside the browser only, so the environment detection sees the real names. Pack images on
// https://cdn.arcavale.com are answered inside the browser with sample tiles drawn here (plain line marks, one per
// slot), so the real CSP applies and nothing is fetched from the network.
// Each page is also checked with axe-core in the real browser (including colour contrast), and the run fails on any
// Content-Security-Policy violation or axe violation.
//
// Usage: npm run build && node scripts/screenshots.mjs [output dir]
// Needs a Chromium build: PLAYWRIGHT_BROWSERS_PATH (default /opt/pw-browsers) or CHROMIUM_PATH.

import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { chromium } from "playwright-core";

const root = join(import.meta.dirname, "..");
const out = process.argv[2] ?? join(root, "..", "docs", "evidence", "panel");
const port = 4181;

function chromiumPath() {
  if (process.env.CHROMIUM_PATH) return process.env.CHROMIUM_PATH;
  const base = process.env.PLAYWRIGHT_BROWSERS_PATH ?? "/opt/pw-browsers";
  const dir = readdirSync(base).filter((d) => /^chromium-\d+$/.test(d)).sort().pop();
  if (!dir) throw new Error(`no chromium-* folder in ${base}`);
  return join(base, dir, "chrome-linux", "chrome");
}

const axeSource = readFileSync(join(root, "node_modules", "axe-core", "axe.min.js"), "utf8");
mkdirSync(out, { recursive: true });
if (!existsSync(join(root, "dist", "index.html"))) throw new Error("run npm run build first");

/** A fresh sample-data server for each theme, so both themes show the same starting state. */
async function startServer() {
  const child = spawn(process.execPath, [join(root, "scripts", "mock-server.mjs"), String(port)], { stdio: ["ignore", "pipe", "inherit"] });
  await new Promise((resolve) => child.stdout.once("data", resolve));
  return child;
}

async function stopServer(child) {
  if (!child || child.exitCode !== null) return;
  const exited = new Promise((resolve) => child.once("exit", resolve));
  child.kill();
  await exited;
}

let server = null;

// ---------------------------------------------------------------------------------------------------------------
// Sample tiles for the pack previews: flat, quiet line marks in the app's palette, one per slot. They stand in for
// pack artwork in the evidence screenshots only and are never shipped.

const TILE_BACKGROUNDS = ["#202833", "#26222f", "#1d2d2a", "#2e2422", "#252b20", "#2a2129", "#1f2632", "#2b2a22"];
const GLYPHS = {
  "role:killer": '<circle cx="128" cy="52" r="12"/><path d="M128 64v86a34 34 0 1 1-34-34"/>',
  "role:survivor": '<circle cx="128" cy="92" r="30"/><path d="M70 200c6-36 30-58 58-58s52 22 58 58"/>',
  "map:generic": '<path d="M40 192 98 112l36 40 32-30 50 70z"/><circle cx="178" cy="78" r="14"/>',
  character: '<circle cx="128" cy="98" r="34"/><path d="M62 206c8-40 34-62 66-62s58 22 66 62"/>',
};
const tileCache = new Map();

function tileSvg(key, n) {
  const kind = key.slice(0, key.indexOf(":"));
  const glyph = GLYPHS[key] ?? (kind === "map" ? GLYPHS["map:generic"] : kind === "role" ? GLYPHS["role:killer"] : GLYPHS.character);
  const accent = kind === "role" || key.includes("generic") ? "#d4a649" : "#c8d0da";
  const background = TILE_BACKGROUNDS[n % TILE_BACKGROUNDS.length];
  return `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256"><rect width="256" height="256" fill="${background}"/><rect x="0.5" y="0.5" width="255" height="255" fill="none" stroke="#ffffff" stroke-opacity="0.06"/><g fill="none" stroke="${accent}" stroke-width="9" stroke-linecap="round" stroke-linejoin="round">${glyph}</g></svg>`;
}

async function sampleTiles() {
  const response = await fetch(`http://127.0.0.1:${port}/api/packs`, { headers: { "X-DBDP-Admin": "1" } });
  const { packs } = await response.json();
  const page = await browser.newPage({ viewport: { width: 256, height: 256 } });
  let n = 0;
  for (const pack of packs) {
    for (const entry of pack.entries) {
      if (tileCache.has(entry.sha256)) continue;
      await page.setContent(`<body style="margin:0">${tileSvg(entry.key, n++)}</body>`);
      tileCache.set(entry.sha256, await page.locator("svg").screenshot({ type: "png" }));
    }
  }
  await page.close();
}

const browser = await chromium.launch({
  executablePath: chromiumPath(),
  args: [
    "--host-resolver-rules=MAP dbdadmin.arcavale.com 127.0.0.1, MAP admin-staging.arcavale.com 127.0.0.1",
    "--disable-features=HttpsUpgrades,HttpsFirstBalancedModeAutoEnable",
    // The local server speaks plain HTTP; treating these two origins as secure makes the browser behave as it does
    // on the real HTTPS sites (it sends Sec-Fetch-Site, applies COOP and allows the clipboard API).
    `--unsafely-treat-insecure-origin-as-secure=http://dbdadmin.arcavale.com:${port},http://admin-staging.arcavale.com:${port}`,
  ],
});

const problems = [];
const shots = [];

async function newPage(theme, { host = "dbdadmin.arcavale.com", width = 1360, height = 900, expectedStatus = null } = {}) {
  const context = await browser.newContext({ colorScheme: theme, viewport: { width, height }, deviceScaleFactor: 1, reducedMotion: "reduce", locale: "en-GB", timezoneId: "Europe/London" });
  context.setDefaultTimeout(15_000);
  const page = await context.newPage();
  page.on("console", (message) => {
    const text = message.text();
    // A meta-delivered CSP cannot carry frame-ancestors; the browser notes that and the _headers copy applies.
    if (/frame-ancestors' is ignored when delivered via a <meta> element/.test(text)) return;
    // The error-state scenario answers with this status on purpose; the browser logs every failed request.
    if (expectedStatus !== null && text.includes(`status of ${expectedStatus}`)) return;
    if (message.type() === "error" || /Content Security Policy/i.test(text)) problems.push(`${theme} ${page.url()}: console ${message.type()}: ${text}`);
  });
  page.on("pageerror", (error) => problems.push(`${theme} ${page.url()}: page error: ${error.message}`));
  await context.route("https://cdn.arcavale.com/packs/**", (route) => {
    const sha = /\/packs\/([0-9a-f]{64})\./.exec(route.request().url())?.[1];
    const tile = sha && tileCache.get(sha);
    return tile ? route.fulfill({ status: 200, contentType: "image/png", body: tile }) : route.fulfill({ status: 404, contentType: "text/plain", body: "Not found" });
  });
  return { context, page, base: `http://${host}:${port}` };
}

async function axeCheck(page, label) {
  await page.evaluate(axeSource);
  const result = await page.evaluate(async () => {
    // eslint-disable-next-line no-undef
    const r = await axe.run(document, { resultTypes: ["violations"] });
    return r.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ")).slice(0, 3).join(", ")}`);
  });
  for (const v of result) problems.push(`${label}: axe ${v}`);
}

async function shot(page, name, theme, fullPage = true) {
  await page.waitForTimeout(150);
  const file = join(out, `${name}-${theme}.png`);
  await page.screenshot({ path: file, fullPage });
  shots.push(file);
  await axeCheck(page, `${name}-${theme}`);
}

async function ready(page, text) {
  await page.getByRole("heading", { level: 1, name: text }).waitFor();
  await page.waitForFunction(() => !document.querySelector(".state-loading"));
}

try {
  for (const theme of ["dark", "light"]) {
    await stopServer(server);
    server = await startServer();
    await sampleTiles();
    // Invites list
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/invites`);
      await ready(page, "Invites");
      await shot(page, "invites", theme);

      // Revoke confirmation
      await page.getByRole("button", { name: "Revoke Community night giveaway" }).click();
      await page.getByRole("dialog").waitFor();
      await shot(page, "invites-revoke-confirm", theme, false);
      await page.keyboard.press("Escape");
      await page.evaluate(() => window.scrollTo(0, 0));

      // New codes shown once
      await page.getByLabel("Number of codes").fill("3");
      await page.getByLabel("Note (optional)").fill("Clan night, 26 October");
      await page.getByRole("button", { name: "Create 3 invites" }).click();
      await page.getByRole("heading", { name: "3 new invite codes" }).waitFor();
      await page.waitForFunction(() => !document.querySelector(".state-loading"));
      await page.getByRole("button", { name: /^Copy code / }).first().focus();
      await shot(page, "invites-new-codes", theme, false);
      await context.close();
    }

    // Users with one user's devices open, then the typed delete confirmation
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/users`);
      await ready(page, "Users");
      await page.locator("tr", { hasText: "Tobias Reed" }).getByRole("button", { name: /active/ }).click();
      await shot(page, "users", theme);
      await page.getByRole("button", { name: "Delete quietfog" }).click();
      await page.getByRole("dialog").waitFor();
      const expected = await page.locator(".dialog label code").textContent();
      await page.getByRole("dialog").getByRole("textbox").fill(expected ?? "");
      await shot(page, "users-delete-confirm", theme, false);
      await page.keyboard.press("Escape");
      await context.close();
    }

    // Audit log with the chain verified
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/audit`);
      await ready(page, "Audit log");
      await page.getByRole("button", { name: "Verify chain" }).click();
      await page.getByText("Chain intact").waitFor();
      await page.locator("tbody tr").first().getByRole("button", { name: /^Show/ }).click();
      await shot(page, "audit", theme);
      await context.close();
    }

    // Access & features, the access-mode confirmation and the emergency switch confirmation
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/access`);
      await ready(page, "Access & features");
      await shot(page, "access", theme);
      await page.getByLabel("Public").check();
      await page.getByRole("button", { name: "Save access mode" }).click();
      await page.getByRole("dialog").waitFor();
      await shot(page, "access-confirm", theme, false);
      await page.keyboard.press("Escape");
      await page.getByRole("button", { name: "Turn online features off" }).click();
      await page.getByRole("dialog").waitFor();
      await shot(page, "access-emergency-confirm", theme, false);
      await page.keyboard.press("Escape");
      await context.close();
    }

    // Packs: the overview, the publish confirmation, the editor with a file chosen, and the withdraw confirmation
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/packs`);
      await ready(page, "Packs");
      await page.waitForFunction(() => [...document.images].every((img) => img.complete && img.naturalWidth > 0));
      await shot(page, "packs", theme);
      await page.getByRole("button", { name: "Publish changes: Backup tiles" }).click();
      await page.getByRole("dialog").waitFor();
      await shot(page, "packs-publish-confirm", theme, false);
      await page.keyboard.press("Escape");
      await page.getByRole("button", { name: "Edit Original" }).click();
      await page.getByRole("heading", { level: 2, name: "Original" }).waitFor();
      await page.locator("#add-image-file").setInputFiles({ name: "the-hag.png", mimeType: "image/png", buffer: tileCache.values().next().value });
      await page.locator("#add-image-slot").selectOption("character:the-hag");
      await page.locator("#add-image-licensed").check();
      await page.waitForFunction(() => [...document.images].every((img) => img.complete && img.naturalWidth > 0));
      await shot(page, "packs-editor", theme);
      await page.getByRole("button", { name: "Withdraw Ghost Face" }).click();
      await page.getByRole("dialog").waitFor();
      await shot(page, "packs-withdraw-confirm", theme, false);
      await page.keyboard.press("Escape");
      await context.close();
    }

    // A new pack
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/packs`);
      await ready(page, "Packs");
      await page.getByRole("button", { name: "New pack" }).first().click();
      await page.getByLabel("Name", { exact: true }).fill("Halloween event");
      await page.getByLabel("Author", { exact: true }).fill("DBD Presence");
      await page.getByLabel("Licence note").fill("Made for DBD Presence.");
      await shot(page, "packs-new", theme);
      await context.close();
    }

    // Characters (the signed roster, D35)
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/characters`);
      await ready(page, "Characters");
      await shot(page, "characters", theme);
      await context.close();
    }

    // Discord applications
    {
      const { context, page, base } = await newPage(theme);
      await page.goto(`${base}/#/apps`);
      await ready(page, "Discord apps");
      await shot(page, "apps", theme);
      await context.close();
    }

    // Packs at a narrow (phone) width
    {
      const { context, page, base } = await newPage(theme, { width: 390, height: 844 });
      await page.goto(`${base}/#/packs`);
      await ready(page, "Packs");
      await page.waitForFunction(() => [...document.images].every((img) => img.complete && img.naturalWidth > 0));
      await shot(page, "narrow-packs", theme, false);
      await context.close();
    }

    // Expired Cloudflare Access session (the API answers 401)
    {
      const { context, page, base } = await newPage(theme, { expectedStatus: 401 });
      await page.route("**/api/users", (route) => route.fulfill({ status: 401, contentType: "application/json; charset=utf-8", body: JSON.stringify({ schemaVersion: 1, error: "unauthenticated" }) }));
      await page.goto(`${base}/#/users`);
      await page.getByRole("alert").first().waitFor();
      await page.waitForFunction(() => !document.querySelector(".state-loading"));
      await shot(page, "error-session-expired", theme, false);
      await context.close();
    }

    // Staging host and a narrow (phone) width
    {
      const { context, page, base } = await newPage(theme, { host: "admin-staging.arcavale.com", width: 390, height: 844 });
      await page.goto(`${base}/#/invites`);
      await ready(page, "Invites");
      await shot(page, "staging-narrow-invites", theme, false);
      await context.close();
    }
  }

} finally {
  await browser.close();
  await stopServer(server);
}

for (const file of shots) console.log(`wrote ${file}`);
if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s):`);
  for (const p of problems) console.error(`- ${p}`);
  process.exit(1);
}
console.log(`\n${shots.length} screenshots; no CSP or axe violations.`);
