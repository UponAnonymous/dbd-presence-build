// Fails when panel code could turn text into markup or script (FR-ADM-04, ADR-0014). Scans the TypeScript sources,
// the tests, the HTML in public/ and, when it exists, the built dist/ output, so nothing can slip in through the
// bundle either. Rendering must use createElement and textContent only (src/dom.ts).
//
// Usage: node scripts/check-dom-sinks.mjs            scan
//        node scripts/check-dom-sinks.mjs --self-test  prove every rule fires on a sample
// Exit code 1 lists each hit as file:line.

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { extname, join, relative } from "node:path";

const root = join(import.meta.dirname, "..");

// Patterns are assembled from pieces so this file does not match itself.
const p = (...parts) => parts.join("");
const CODE_RULES = [
  [new RegExp(p("\\.inner", "HTML\\b")), "inner" + "HTML"],
  [new RegExp(p("\\.outer", "HTML\\b")), "outer" + "HTML"],
  [new RegExp(p("insertAdjacent", "HTML")), "insertAdjacent" + "HTML"],
  [new RegExp(p("document\\s*\\.\\s*write", "(ln)?\\s*\\(")), "document." + "write"],
  [new RegExp(p("createContextual", "Fragment")), "createContextual" + "Fragment"],
  [new RegExp(p("DOM", "Parser")), "DOM" + "Parser"],
  [new RegExp(p("setHTML", "Unsafe|parseHTML", "Unsafe")), "set/parseHTML" + "Unsafe"],
  [new RegExp(p("\\bsrc", "doc\\b")), "src" + "doc"],
  [new RegExp(p("\\bev", "al\\s*\\(")), "ev" + "al("],
  [new RegExp(p("new\\s+Func", "tion\\s*\\(")), "new Func" + "tion("],
  [new RegExp(p("\\.on[a-z]+\\s*=", "(?!=)")), "on* property handler (use addEventListener)"],
  [new RegExp(p("java", "script:"), "i"), "java" + "script: URL"],
];
const HTML_RULES = [
  [new RegExp(p("<scr", "ipt(?![^>]*\\ssrc=)[^>]*>"), "i"), "inline <scr" + "ipt>"],
  [new RegExp(p("\\son[a-z]+\\s*", "="), "i"), "inline event handler attribute"],
  [new RegExp(p("\\ssty", "le\\s*="), "i"), "inline sty" + "le attribute"],
  [new RegExp(p("<sty", "le[\\s>]"), "i"), "inline <sty" + "le> element"],
  [new RegExp(p("java", "script:"), "i"), "java" + "script: URL"],
];

function files(dir, extensions) {
  if (!existsSync(dir)) return [];
  const out = [];
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) out.push(...files(path, extensions));
    else if (extensions.includes(extname(name))) out.push(path);
  }
  return out;
}

function scan(text, rules) {
  const hits = [];
  text.split("\n").forEach((line, index) => {
    for (const [pattern, name] of rules) if (pattern.test(line)) hits.push({ line: index + 1, name });
  });
  return hits;
}

if (process.argv.includes("--self-test")) {
  const samples = [
    [p("el.inner", "HTML = x"), CODE_RULES],
    [p("el.outer", "HTML = x"), CODE_RULES],
    [p("el.insertAdjacent", "HTML('a', x)"), CODE_RULES],
    [p("document.wri", "te(x)"), CODE_RULES],
    [p("range.createContextual", "Fragment(x)"), CODE_RULES],
    [p("new DOM", "Parser()"), CODE_RULES],
    [p("el.setHTML", "Unsafe(x)"), CODE_RULES],
    [p("frame.src", "doc = x"), CODE_RULES],
    [p("ev", "al(x)"), CODE_RULES],
    [p("new Func", "tion(x)"), CODE_RULES],
    [p("button.on", "click = f"), CODE_RULES],
    [p("a.href = 'java", "script:x'"), CODE_RULES],
    [p("<scr", "ipt>x</script>"), HTML_RULES],
    [p("<div on", "click=\"x\">"), HTML_RULES],
    [p("<p sty", "le=\"x\">"), HTML_RULES],
    [p("<sty", "le>p{}</style>"), HTML_RULES],
  ];
  const missed = samples.filter(([text, rules]) => scan(text, rules).length === 0);
  const clean = [
    ["el.textContent = x; el.addEventListener('click', f); if (a.on === b) {}", CODE_RULES],
    ['<script type="module" src="/assets/panel.js"></script>', HTML_RULES],
  ].filter(([text, rules]) => scan(text, rules).length > 0);
  if (missed.length || clean.length) {
    for (const [text] of missed) console.error(`self-test: not detected: ${text}`);
    for (const [text] of clean) console.error(`self-test: false positive: ${text}`);
    process.exit(1);
  }
  console.log(`DOM sink check self-test OK: ${samples.length} samples detected, no false positives.`);
  process.exit(0);
}

const targets = [
  ...files(join(root, "src"), [".ts"]).map((f) => [f, CODE_RULES]),
  ...files(join(root, "test"), [".ts"]).map((f) => [f, CODE_RULES]),
  ...files(join(root, "public"), [".html"]).map((f) => [f, HTML_RULES]),
  ...files(join(root, "dist"), [".js"]).map((f) => [f, CODE_RULES]),
  ...files(join(root, "dist"), [".html"]).map((f) => [f, HTML_RULES]),
];

let problems = 0;
for (const [file, rules] of targets) {
  for (const hit of scan(readFileSync(file, "utf8"), rules)) {
    problems++;
    console.error(`${relative(root, file)}:${hit.line}: ${hit.name}`);
  }
}
if (problems > 0) {
  console.error(`DOM sink check FAILED: ${problems} problem(s). Render with createElement and textContent (src/dom.ts).`);
  process.exit(1);
}
console.log(`DOM sink check OK: ${targets.length} files, no HTML-parsing, script-evaluating or inline-handler code.`);
