// Microsoft Store listing: public privacy-policy and terms URLs (docs/legal/*.md served as static HTML on the API host).
// NFR-SVC-01 (security headers, 404 for unknown paths). The sync test fails when src/legal-pages.ts is stale.

import { env } from "cloudflare:test";
import { describe, expect, it, inject } from "vitest";
import { handleRequest } from "../src/app.ts";
import { PRIVACY_PAGE, TERMS_PAGE } from "../src/legal-pages.ts";
import { escapeHtml, renderInline, renderLegalModule, renderMarkdown, safeHref } from "../scripts/legal-render.mjs";
import { ADMIN, API, Harness, resetDatabase } from "./helpers.ts";

async function get(url: string, method = "GET"): Promise<Response> {
  const h = new Harness();
  return h.call(method, url);
}

describe("GET /privacy and /terms", () => {
  for (const [path, page] of [["/privacy", PRIVACY_PAGE], ["/terms", TERMS_PAGE]] as const) {
    it(`serves ${path} as static, safe HTML`, async () => {
      await resetDatabase();
      const response = await get(`${API}${path}`);
      expect(response.status).toBe(200);
      expect(response.headers.get("Content-Type")).toBe("text/html; charset=utf-8");
      expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
      expect(response.headers.get("Content-Security-Policy")).toContain("default-src 'none'; style-src 'unsafe-inline'");
      expect(response.headers.get("Content-Security-Policy")).not.toMatch(/script|img|connect/);
      expect(response.headers.get("Cache-Control")).toBe("public, max-age=3600");
      expect(response.headers.get("Set-Cookie")).toBeNull();
      const html = await response.text();
      expect(html).toContain(`<h1>${page.title}</h1>`);
      expect(html).toContain("unofficial, fan-made app. It is not affiliated with or endorsed by Behaviour Interactive or Discord.");
      expect(html).not.toMatch(/<script|<img|<link|<iframe|src=|javascript:|\son\w+=/i);
      for (const href of html.matchAll(/href="([^"]*)"/g)) expect(href[1]).toMatch(/^(https:|mailto:|\/privacy$|\/terms$)/);
    });
  }

  it("answers HEAD with the same headers and no body, and other methods 405", async () => {
    const head = await get(`${API}/privacy`, "HEAD");
    expect(head.status).toBe(200);
    expect(head.headers.get("Content-Type")).toBe("text/html; charset=utf-8");
    expect(await head.text()).toBe("");
    const post = await get(`${API}/terms`, "POST");
    expect(post.status).toBe(405);
    expect(post.headers.get("Allow")).toBe("GET, HEAD");
  });

  it("keeps every other path a 404 and serves the pages on the API host only", async () => {
    for (const url of [`${API}/`, `${API}/privacy/`, `${API}/terms.html`, `${API}/Privacy`, `${API}/v1/privacy`, `${API}/constructor`, `${ADMIN}/privacy`, "https://cdn.arcavale.com/privacy"]) {
      const response = await get(url);
      expect(response.status, url).toBe(404);
      expect(response.headers.get("Content-Security-Policy"), url).not.toContain("style-src");
    }
  });

  it("keeps the strict JSON CSP on API responses", async () => {
    const response = await handleRequest(new Request(`${API}/v1/nope`), env, new Harness().deps);
    expect(response.headers.get("Content-Security-Policy")).toBe("default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
  });
});

describe("legal renderer", () => {
  it("escapes hostile text everywhere", () => {
    const hostile = `<script>alert(1)</script> "q" 'a' & **<b>x</b>** \`<i>\` [<u>l</u>](https://e.com/?a=1&b="2")`;
    const html = renderInline(hostile);
    expect(html).not.toMatch(/<script|<b>|<i>|<u>/);
    expect(html).toContain("&lt;script&gt;alert(1)&lt;/script&gt;");
    expect(escapeHtml(`<>&"'`)).toBe("&lt;&gt;&amp;&quot;&#39;");
    const doc = renderMarkdown(`# <img src=x onerror=1>\n\n## <svg/onload=1>\n- <iframe>\n  - nested & more\n`);
    expect(doc.title).toBe("<img src=x onerror=1>");
    expect(doc.html).not.toMatch(/<svg|<iframe/);
  });

  it("allows only https: and mailto: links", () => {
    for (const bad of ["javascript:alert1", "http://example.com", "data:text/html,x", "//evil.com", "JaVaScRiPt:x", "ftp://x.y", "https://", "https://a b.com", "/relative"]) {
      expect(safeHref(bad), bad).toBeNull();
      if (!bad.includes(" ")) expect(renderInline(`[click](${bad})`), bad).toBe("click");
    }
    expect(renderInline("[a](https://example.com/x)")).toBe('<a href="https://example.com/x" rel="noopener noreferrer">a</a>');
    expect(renderInline("[m](mailto:a@b.co)")).toContain('href="mailto:a@b.co"');
  });

  it("renders headings, paragraphs, nested bullets, bold and code, and carries a Last-updated line through", () => {
    const doc = renderMarkdown("# T\n\nLast updated: 1 January 2027\n\n## H\nline one\nline two **b** `c`\n\n- a\n  continued\n  - b\n- c\n");
    expect(doc.title).toBe("T");
    expect(doc.html).toBe("<p>Last updated: 1 January 2027</p>\n<h2>H</h2>\n<p>line one line two <strong>b</strong> <code>c</code></p>\n<ul><li>a continued<ul><li>b</li></ul></li><li>c</li></ul>\n");
  });
});

describe("generated module", () => {
  it("is in sync with docs/legal/*.md (run `npm run build-legal` in service/ to refresh)", () => {
    expect(inject("legalPagesModule")).toBe(renderLegalModule(inject("privacyNoticeMarkdown"), inject("termsMarkdown")));
  });

  it("states the unofficial line on the privacy page", () => {
    expect(PRIVACY_PAGE.body).toContain("not affiliated with or endorsed by Behaviour Interactive or Discord");
  });
});
