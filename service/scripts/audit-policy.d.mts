// Types for scripts/audit-policy.mjs (used by test/audit-policy.test.ts).

export interface Advisory {
  id: string;
  package: string;
  severity: string;
  title: string;
  range: string;
}

export interface AuditException {
  advisory: string;
  package: string;
  added?: string;
  expires?: string;
  [key: string]: unknown;
}

export const MAX_EXCEPTION_DAYS: number;
export function productionPackages(lock: { packages?: Record<string, { dev?: boolean; devOptional?: boolean }> }): Set<string>;
export function exceptionProblem(exception: AuditException, pkg: string, production: Set<string>, today: string): string | null;
export function evaluate(advisories: Advisory[], exceptions: AuditException[], production: Set<string>, today: string): { failed: boolean; lines: string[]; unused: AuditException[] };
