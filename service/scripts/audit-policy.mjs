// The audit gate's policy (scripts/audit-gate.mjs), kept free of Node APIs so the Vitest suite can test it.
//
// An advisory at level high or critical fails the gate unless an exception in audit-exceptions.json matches it by
// advisory id AND package name, the package is development-only (not in the production dependency tree of
// package-lock.json), the exception has an `added` date and an `expires` date at most 30 days after it, and that
// expiry has not passed. Anything else about an exception is a failure, not a pass.

export const MAX_EXCEPTION_DAYS = 30;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Names of every package in the production dependency tree of a lockfile v2/v3 (`dev`/`devOptional` excluded). */
export function productionPackages(lock) {
  const names = new Set();
  for (const [path, entry] of Object.entries(lock.packages ?? {})) {
    if (path === "" || entry.dev === true || entry.devOptional === true) continue;
    const at = path.lastIndexOf("node_modules/");
    if (at >= 0) names.add(path.slice(at + "node_modules/".length));
  }
  return names;
}

function addDays(date, days) {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** Why `exception` cannot cover an advisory in `pkg`, or null when it may. */
export function exceptionProblem(exception, pkg, production, today) {
  if (exception.package !== pkg) return `exception is for package ${exception.package}, not ${pkg}`;
  if (production.has(pkg)) return `${pkg} is in the production dependency tree; only development-only packages may be excepted`;
  if (typeof exception.added !== "string" || !DATE.test(exception.added)) return "exception has no valid added date";
  if (typeof exception.expires !== "string" || !DATE.test(exception.expires)) return "exception has no valid expires date";
  if (exception.expires > addDays(exception.added, MAX_EXCEPTION_DAYS)) return `exception expires more than ${MAX_EXCEPTION_DAYS} days after it was added`;
  if (exception.expires < today) return `exception expired on ${exception.expires}`;
  return null;
}

/**
 * advisories: [{ id, package, severity, title, range }]; exceptions: audit-exceptions.json `exceptions`.
 * Returns { failed, lines, unused } where `unused` lists exceptions that matched no advisory.
 */
export function evaluate(advisories, exceptions, production, today) {
  let failed = false;
  const lines = [];
  const used = new Set();
  for (const advisory of [...advisories].sort((a, b) => a.id.localeCompare(b.id) || a.package.localeCompare(b.package))) {
    const gating = advisory.severity === "high" || advisory.severity === "critical";
    let verdict;
    if (!gating) verdict = "below threshold";
    else {
      const candidates = exceptions.filter((e) => e.advisory === advisory.id);
      const match = candidates.find((e) => e.package === advisory.package);
      const problem = match ? exceptionProblem(match, advisory.package, production, today) : candidates.length > 0 ? `exception is for package ${candidates[0].package}, not ${advisory.package}` : "not listed in audit-exceptions.json";
      if (problem) {
        verdict = `FAIL: ${problem}`;
        failed = true;
      } else {
        verdict = `excepted until ${match.expires}`;
        used.add(match);
      }
    }
    lines.push(`${advisory.severity.padEnd(8)} ${advisory.id} ${advisory.package} ${advisory.range}: ${advisory.title} -> ${verdict}`);
  }
  return { failed, lines, unused: exceptions.filter((e) => !used.has(e)) };
}
