import "vitest";

declare module "vitest" {
  export interface ProvidedContext {
    adr0014: string;
    pagesHeaders: string;
    indexHtml: string;
    stagingHeaders: string;
    stagingIndexHtml: string;
    /** The app's slot sources (test/catalog.test.ts). */
    vocabulary: string;
    rosterSeed: string;
    importNames: string;
    knownSlugs: string;
    automaticIcons: string;
    packItemViewModels: string;
  }
}
