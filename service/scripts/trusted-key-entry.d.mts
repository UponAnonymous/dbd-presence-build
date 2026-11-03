// Types for scripts/trusted-key-entry.mjs (used by test/key-ring.test.ts and test/provision.test.ts).
export function purposeForKid(kid: string): string | null;
export function listForKid(kid: string): "staging" | "production" | null;
export function trustedKeyEntry(kid: string, purpose: string, spkiBase64Url: string): string;
export function publicKeyNotice(kid: string, spkiBase64Url: string): string;
