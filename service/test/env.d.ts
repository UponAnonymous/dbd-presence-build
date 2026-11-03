import type { D1Migration } from "cloudflare:test";
import type { Env as ServiceEnv } from "../src/env.ts";

interface FixtureFile {
  file: string;
  text: string;
}

interface WranglerSummary {
  name: string;
  workersDev: boolean;
  previewUrls: boolean;
  routes: { pattern: string; custom_domain?: boolean; zone_name?: string }[];
  vars: Record<string, string>;
  d1: { binding: string; name: string; id: string | null }[];
  r2: { binding: string; name: string }[];
  secrets: string[];
  crons: string[];
}

declare global {
  namespace Cloudflare {
    interface Env extends ServiceEnv {
      TEST_MIGRATIONS: D1Migration[];
    }
  }
}

declare module "vitest" {
  export interface ProvidedContext {
    signingKeys: string;
    signingVectors: FixtureFile[];
    crossStackDocuments: FixtureFile[];
    publishedIndexFixture: string;
    uploadsContract: string;
    rosterSeed: string;
    rosterFoldCases: string;
    signedRoster: string;
    vocabulary: string;
    migrationNames: string[];
    /** JSON `[{kid, purposes, spki, pkcs8}]`: the per-run offline release and recovery keys (private halves for signing in tests only). */
    offlineSigningKeys: string;
    serviceSources: FixtureFile[];
    privacyNoticeMarkdown: string;
    termsMarkdown: string;
    legalPagesModule: string;
    trustedServiceKeys: string;
    ciServiceWorkflow: string;
    backupWorkflow: string;
    backupScript: string;
    backupGuide: string;
    ownerGuide: string;
    wranglerProduction: WranglerSummary;
    wranglerStaging: WranglerSummary;
  }
}
