// Regenerates src/legal-pages.ts from docs/legal/*.md (the deployed Worker cannot read docs/).
// Usage: node scripts/build-legal.mjs [--check]   (--check exits 1 if the committed module is stale)
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { renderLegalModule } from "./legal-render.mjs";

const root = join(import.meta.dirname, "..", "..");
const target = join(import.meta.dirname, "..", "src", "legal-pages.ts");
const text = renderLegalModule(readFileSync(join(root, "docs", "legal", "privacy-notice.md"), "utf8"), readFileSync(join(root, "docs", "legal", "terms-of-use.md"), "utf8"));

if (process.argv.includes("--check")) {
  if (readFileSync(target, "utf8") !== text) {
    console.error("src/legal-pages.ts is out of date with docs/legal/*.md: run `npm run build-legal`.");
    process.exit(1);
  }
} else {
  writeFileSync(target, text);
}
