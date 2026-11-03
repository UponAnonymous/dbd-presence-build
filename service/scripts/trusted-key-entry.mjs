// The line the owner pastes into src/DbdPresence.Online/TrustedServiceKeys.json for a new signing key
// (scripts/new-secret.mjs signing-key). No Node APIs, so the Vitest suite checks the format.
//
// TrustedServiceKeys.json has two lists, "staging" and "production"; each entry is
//   { "kid": "<name>", "purposes": ["<purpose>"], "spki": "<base64url SubjectPublicKeyInfo>" }
// and a key may sign only the purposes listed for it (ADR-0012 rule 2a). The purpose and the list follow from the
// key name the owner guide uses: the purpose itself (licence-, config- or pack-index-), then -staging- or -prod-,
// then a number. The app reserves the staging entry "pack-index-staging-1" for the content key, which also signs the
// character roster (ADR-0012 addendum, PLAN_V2_S5B §2.3), so its line lists both purposes.

const PURPOSES = ["licence", "config", "pack-index"];

/** Every purpose a key of the given name purpose may sign: the content key (pack-index) also signs the roster. */
export function purposesFor(purpose) {
  return purpose === "pack-index" ? ["pack-index", "roster"] : [purpose];
}

/** The purpose a key name implies, or null when the name does not start with licence-, config- or pack-index-. */
export function purposeForKid(kid) {
  return PURPOSES.find((p) => kid.startsWith(`${p}-`)) ?? null;
}

/** "staging", "production" or null, from the key name. */
export function listForKid(kid) {
  if (/-staging-/.test(kid)) return "staging";
  if (/-prod-/.test(kid)) return "production";
  return null;
}

/** The JSON line for TrustedServiceKeys.json. */
export function trustedKeyEntry(kid, purpose, spkiBase64Url) {
  return `{ "kid": ${JSON.stringify(kid)}, "purposes": [${purposesFor(purpose).map((p) => JSON.stringify(p)).join(", ")}], "spki": ${JSON.stringify(spkiBase64Url)} }`;
}

/**
 * What `new-secret.mjs signing-key` prints on standard error: the list to edit and the exact line to paste. The deploy
 * workflow reads the public key back from this text (provision-plan.mjs publicKeyFromGeneratorOutput), so both sides
 * use this one function.
 */
export function publicKeyNotice(kid, spkiBase64Url) {
  return (
    `Public key for kid "${kid}" (not secret). Add this line to the "${listForKid(kid)}" list in src/DbdPresence.Online/TrustedServiceKeys.json:\n` +
    `    ${trustedKeyEntry(kid, purposeForKid(kid), spkiBase64Url)}`
  );
}
