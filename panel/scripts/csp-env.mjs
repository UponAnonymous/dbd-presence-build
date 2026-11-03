// The one difference between the production and staging builds of the panel (ADR-0014): the content host in the
// CSP's img-src. Used by scripts/build.mjs and checked by test/security.test.ts.

export const CDN_HOSTS = { production: "https://cdn.arcavale.com", staging: "https://cdn-staging.arcavale.com" };

/** `text` (public/_headers or public/index.html) with img-src naming the given environment's content host. */
export function forEnvironment(text, environment) {
  if (!Object.hasOwn(CDN_HOSTS, environment)) throw new Error(`unknown environment ${environment}`);
  const production = `img-src 'self' ${CDN_HOSTS.production};`;
  if (!text.includes(production)) throw new Error("the production img-src was not found");
  return text.replaceAll(production, `img-src 'self' ${CDN_HOSTS[environment]};`);
}
