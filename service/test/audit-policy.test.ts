// NFR-SVC-09 (dependency audit gate): exceptions match advisory and package, cover development-only packages only,
// and last at most 30 days from when they were added (scripts/audit-policy.mjs, used by scripts/audit-gate.mjs).

import { describe, expect, it } from "vitest";
import { evaluate, productionPackages, type Advisory, type AuditException } from "../scripts/audit-policy.mjs";

const lock = {
  packages: {
    "": {},
    "node_modules/zod": { dev: true },
    "node_modules/undici": { devOptional: true },
    "node_modules/runtime-lib": {},
    "node_modules/@scope/inner": {},
  },
};
const production = productionPackages(lock);
const high = (id: string, pkg: string): Advisory => ({ id, package: pkg, severity: "high", title: "t", range: "<1" });
const exception = (over: Partial<AuditException> = {}): AuditException => ({ advisory: "GHSA-1", package: "zod", added: "2026-10-24", expires: "2026-11-01", ...over });

describe("audit gate policy", () => {
  it("reads the production tree from the lockfile, leaving out dev and devOptional packages", () => {
    expect([...production].sort()).toEqual(["@scope/inner", "runtime-lib"]);
  });

  it("passes a valid exception for a development-only package", () => {
    expect(evaluate([high("GHSA-1", "zod")], [exception()], production, "2026-10-25").failed).toBe(false);
  });

  it("matches by advisory id and package name: the same advisory in another package is not excepted", () => {
    const result = evaluate([high("GHSA-1", "zod"), high("GHSA-1", "undici")], [exception()], production, "2026-10-25");
    expect(result.failed).toBe(true);
    expect(result.lines.find((l) => l.includes("undici"))).toContain("FAIL");
  });

  it("refuses an exception for a package in the production tree", () => {
    const result = evaluate([high("GHSA-2", "runtime-lib")], [exception({ advisory: "GHSA-2", package: "runtime-lib" })], production, "2026-10-25");
    expect(result.failed).toBe(true);
    expect(result.lines[0]).toContain("production dependency tree");
  });

  it("caps an exception at 30 days from its added date and requires that date", () => {
    expect(evaluate([high("GHSA-1", "zod")], [exception({ expires: "2026-11-23" })], production, "2026-10-25").failed).toBe(false);
    expect(evaluate([high("GHSA-1", "zod")], [exception({ expires: "2026-11-24" })], production, "2026-10-25").failed).toBe(true);
    const { added: _added, ...undated } = exception();
    expect(evaluate([high("GHSA-1", "zod")], [undated], production, "2026-10-25").failed).toBe(true);
  });

  it("fails an expired exception and ignores advisories below high", () => {
    expect(evaluate([high("GHSA-1", "zod")], [exception()], production, "2026-11-02").failed).toBe(true);
    expect(evaluate([{ ...high("GHSA-3", "runtime-lib"), severity: "moderate" }], [], production, "2026-10-25").failed).toBe(false);
  });
});
