// CI gate equivalent to `npm audit --audit-level=high`, with dated exceptions (audit-exceptions.json).
// The policy is in scripts/audit-policy.mjs: an exception must match the advisory id and the package name, the package
// must be development-only (not in package-lock.json's production tree), and it may last at most 30 days from its
// `added` date. Fails on any unmatched or invalid exception, or when npm audit itself fails. Moderate and low
// advisories are printed but do not fail the gate (the same threshold as --audit-level=high).

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { evaluate, productionPackages } from "./audit-policy.mjs";

const root = join(import.meta.dirname, "..");
const today = new Date().toISOString().slice(0, 10);
const { exceptions } = JSON.parse(readFileSync(join(root, "audit-exceptions.json"), "utf8"));
const production = productionPackages(JSON.parse(readFileSync(join(root, "package-lock.json"), "utf8")));

let output;
try {
  output = execFileSync("npm", ["audit", "--json"], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
} catch (error) {
  // npm audit exits non-zero whenever it finds anything; its JSON report is still on stdout.
  output = error.stdout;
}

let report;
try {
  report = JSON.parse(output);
} catch {
  console.error("audit gate: npm audit did not return a JSON report");
  process.exit(1);
}
if (report.error || !report.vulnerabilities) {
  console.error(`audit gate: npm audit failed: ${JSON.stringify(report.error ?? report).slice(0, 500)}`);
  process.exit(1);
}

const advisories = new Map();
for (const [name, entry] of Object.entries(report.vulnerabilities)) {
  for (const via of entry.via) {
    if (typeof via !== "object") continue;
    const id = (via.url ?? "").split("/").pop() || String(via.source);
    advisories.set(`${id}|${name}`, { id, package: name, severity: via.severity, title: via.title, range: via.range });
  }
}

const { failed, lines, unused } = evaluate([...advisories.values()], exceptions, production, today);
for (const line of lines) console.log(line);
for (const e of unused) console.log(`note: exception ${e.advisory} (${e.package}) is no longer needed; remove it from audit-exceptions.json`);

const counts = report.metadata?.vulnerabilities ?? {};
console.log(`audit gate: ${JSON.stringify(counts)} -> ${failed ? "FAILED" : "passed"}`);
process.exit(failed ? 1 : 0);
