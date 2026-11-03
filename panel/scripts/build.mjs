// Builds the static site into dist/: the bundled script and stylesheet under assets/, plus index.html, _headers and
// the icon from public/. No source maps are published, and nothing is fetched from the network.
//
//   node scripts/build.mjs                  production: images only from https://cdn.arcavale.com
//   node scripts/build.mjs --env staging    staging: images only from https://cdn-staging.arcavale.com
//
// The Content-Security-Policy is ADR-0014's; the only difference between the two builds is the content host in
// img-src, so each panel can show pack previews from its own environment's host and no other (ADR-0014).

import { build } from "esbuild";
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { CDN_HOSTS, forEnvironment } from "./csp-env.mjs";

const root = join(import.meta.dirname, "..");
const dist = join(root, "dist");
const envIndex = process.argv.indexOf("--env");
const environment = envIndex >= 0 ? process.argv[envIndex + 1] : "production";
if (environment !== "production" && environment !== "staging") {
  console.error("usage: node scripts/build.mjs [--env production|staging]");
  process.exit(2);
}

rmSync(dist, { recursive: true, force: true });
mkdirSync(join(dist, "assets"), { recursive: true });
cpSync(join(root, "public"), dist, { recursive: true });
for (const file of ["_headers", "index.html"]) {
  const path = join(dist, file);
  writeFileSync(path, forEnvironment(readFileSync(path, "utf8"), environment));
}

await build({
  entryPoints: { panel: join(root, "src", "main.ts"), "panel-styles": join(root, "src", "styles.css") },
  outdir: join(dist, "assets"),
  entryNames: "[name]",
  bundle: true,
  format: "esm",
  target: ["chrome120", "firefox121", "safari17", "edge120"],
  minify: true,
  sourcemap: false,
  legalComments: "none",
  charset: "utf8",
  logLevel: "info",
});

// esbuild names CSS entry output after the entry; publish it as assets/panel.css, the name index.html links.
cpSync(join(dist, "assets", "panel-styles.css"), join(dist, "assets", "panel.css"));
rmSync(join(dist, "assets", "panel-styles.css"));
console.log(`Built the ${environment} panel: images only from ${CDN_HOSTS[environment]}.`);
