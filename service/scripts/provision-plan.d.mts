// Types for scripts/provision-plan.mjs (used by test/provision.test.ts and test/backups.test.ts).

export type Environment = "staging" | "production";

export interface GenerateItem {
  name: string;
  kind: "signing-key" | "invite-keys" | "ban-key";
  purpose: string | null;
  kid: string;
  reason: "missing" | "rotate";
}

export interface SecretPlan {
  generate: GenerateItem[];
  kept: string[];
  refused: { name: string; reason: string }[];
  ownerMustEnter: { name: string; source: string }[];
  unhandled: string[];
}

export interface GeneratedSecret {
  name: string;
  kid: string;
  purpose: string | null;
  reason: string;
  spki: string | null;
}

export interface TrustedKeyEntry {
  kid: string;
  purposes: string[];
  spki: string;
}

export interface RuleMerge {
  action: "keep" | "add" | "conflict";
  rules: Record<string, unknown>[];
}

export interface ConfigInspection {
  ok: boolean;
  reason?: string;
  kid?: string;
  expiresAt?: string;
  signatureChecked?: boolean;
}

export interface SmokeResult {
  check: string;
  result: string;
  detail: string;
}

export const ENVIRONMENTS: Environment[];
export const KID_ENV: Record<Environment, string>;
export const PAGES_PROJECTS: Record<Environment, string>;
export const HOSTS: Record<Environment, { api: string; admin: string }>;
export const PROTECTED_RECORD: { name: string; type: string; value: string };
export const D1_LOCATION: string;
export const AUDIT_BINDING: string;
export const AUDIT_DAYS: number;
export const AUDIT_LOCK_RULE_ID: string;
export const AUDIT_LIFECYCLE_RULE_ID: string;
export const GENERATED_SECRETS: Record<string, { kind: string; kidPrefix: string; purpose: string | null; rotation: string | null }>;
export const OWNER_SECRETS: Record<string, string>;

export function assertEnvironment(env: string): Environment;
export function kidFor(prefix: string, env: string, options?: { rotation?: boolean; runNumber?: string | number }): string;
export function planSecrets(input: { env: string; required: string[]; existing: string[]; rotate?: boolean; runNumber?: string | number }): SecretPlan;
export function readyToDeploy(plan: SecretPlan): boolean;
export function publicKeyFromGeneratorOutput(stderr: string, kid: string): string;
export function valuesToMask(secretText: string): string[];
export function trustedKeyEntries(generated: GeneratedSecret[]): TrustedKeyEntry[];
export function resourcesFromConfig(
  config: Record<string, unknown>,
  env: string,
): {
  workerName: string;
  database: { binding: string; name: string; configuredId: string | null };
  buckets: { binding: string; name: string }[];
  requiredSecrets: string[];
  routes: unknown[];
};
export function withDatabaseId(rawConfig: Record<string, unknown>, env: string, databaseName: string, databaseId: string): Record<string, unknown>;
export function configDifferences(original: Record<string, unknown>, generated: Record<string, unknown>): string[];
export function lockRule(id: string, days: number): Record<string, unknown>;
export function auditLockRule(): Record<string, unknown>;
export function lifecycleRule(id: string, days: number): Record<string, unknown>;
export function auditLifecycleRule(): Record<string, unknown>;
export function planLockRules(existing: unknown, wanted?: Record<string, unknown>): RuleMerge;
export function planLifecycleRules(existing: unknown, wanted?: Record<string, unknown>): RuleMerge;
export function inspectConfigResponse(bodyText: string, options?: { now?: number; trustedKeys?: { kid: string; spki: string }[] }): Promise<ConfigInspection>;
export function renderSummary(input: { env: string; state?: Record<string, unknown>; outcomes?: Record<string, string>; smoke?: SmokeResult[] }): string;
export function parseSmokeResults(text: string): SmokeResult[];
