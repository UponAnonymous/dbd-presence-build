// Unit and rendering tests in jsdom. Files the tests compare against (ADR-0014's CSP, the Pages headers and the HTML
// shell, and the app's own slot sources for test/catalog.test.ts) are read here and handed to the tests, so the test
// code itself needs no Node APIs.

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { defineConfig } from "vitest/config";
import { forEnvironment } from "./scripts/csp-env.mjs";

const here = import.meta.dirname;
const repo = (...parts) => readFileSync(join(here, "..", ...parts), "utf8");
process.env.TZ = "UTC";

export default defineConfig({
  test: {
    environment: "jsdom",
    environmentOptions: { jsdom: { url: "https://dbdadmin.arcavale.com/" } },
    include: ["test/**/*.test.ts"],
    setupFiles: ["./test/setup.ts"],
    provide: {
      adr0014: readFileSync(join(here, "..", "docs", "DECISIONS", "ADR-0014-owner-panel-and-admin-auth.md"), "utf8"),
      pagesHeaders: readFileSync(join(here, "public", "_headers"), "utf8"),
      indexHtml: readFileSync(join(here, "public", "index.html"), "utf8"),
      stagingHeaders: forEnvironment(readFileSync(join(here, "public", "_headers"), "utf8"), "staging"),
      stagingIndexHtml: forEnvironment(readFileSync(join(here, "public", "index.html"), "utf8"), "staging"),
      vocabulary: repo("src", "DbdPresence.Vision", "Vocabulary", "vocabulary.en.json"),
      rosterSeed: repo("tests", "fixtures", "roster", "seed.json"),
      importNames: repo("tests", "fixtures", "roster", "import-names.json"),
      knownSlugs: repo("src", "DbdPresence.Packs", "Import", "KnownSlugs.cs"),
      automaticIcons: repo("src", "DbdPresence.Core", "Composition", "AutomaticIcons.cs"),
      packItemViewModels: repo("src", "DbdPresence.Presentation", "ViewModels", "PackItemViewModels.cs"),
    },
  },
});
