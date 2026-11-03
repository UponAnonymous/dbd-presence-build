// CI gate equivalent to `npm audit --audit-level=high`, with dated exceptions (audit-exceptions.json).
// Fails when a high or critical advisory is not listed, when its exception has expired, or when npm audit itself fails.
// Moderate and low advisories are printed but do not fail the gate (the same threshold as --audit-level=high).

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";

const root = join(import.meta.dirname, "..");
const today = new Date().toISOString().slice(0, 10);
const { exceptions } = JSON.parse(readFileSync(join(root, "audit-exceptions.json"), "utf8"));
const byId = new Map(exceptions.map((e) => [e.advisory, e]));

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
    advisories.set(id, { id, package: name, severity: via.severity, title: via.title, range: via.range });
  }
}

let failed = false;
const used = new Set();
for (const advisory of [...advisories.values()].sort((a, b) => a.id.localeCompare(b.id))) {
  const gating = advisory.severity === "high" || advisory.severity === "critical";
  const exception = byId.get(advisory.id);
  let verdict;
  if (!gating) verdict = "below threshold";
  else if (!exception) {
    verdict = "FAIL: not listed in audit-exceptions.json";
    failed = true;
  } else if (exception.expires < today) {
    verdict = `FAIL: exception expired on ${exception.expires}`;
    failed = true;
  } else {
    verdict = `excepted until ${exception.expires}`;
    used.add(advisory.id);
  }
  console.log(`${advisory.severity.padEnd(8)} ${advisory.id} ${advisory.package} ${advisory.range}: ${advisory.title} -> ${verdict}`);
}
for (const id of byId.keys()) {
  if (!used.has(id)) console.log(`note: exception ${id} is no longer needed; remove it from audit-exceptions.json`);
}

const counts = report.metadata?.vulnerabilities ?? {};
console.log(`audit gate: ${JSON.stringify(counts)} -> ${failed ? "FAILED" : "passed"}`);
process.exit(failed ? 1 : 0);
