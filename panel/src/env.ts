// Which deployment this page is (FR-ADM-06). Staging and production are separate sites with separate data; any host
// whose name contains "-staging" is treated as staging and shows the banner.

export type Environment = "production" | "staging";

export function environmentFor(hostname: string): Environment {
  return hostname.toLowerCase().includes("-staging") ? "staging" : "production";
}
