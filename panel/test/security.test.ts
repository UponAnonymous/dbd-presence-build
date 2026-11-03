// FR-ADM-04: the Content-Security-Policy is ADR-0014's, verbatim, in both the Pages headers and the meta tag; the HTML
// shell has no inline script, style or handler; nothing is loaded from another origin.

import { describe, expect, inject, it } from "vitest";

function adrPolicy(): string {
  const match = /```\s*\n\s*(default-src[^\n]+)\n\s*```/.exec(inject("adr0014"));
  if (!match) throw new Error("CSP block not found in ADR-0014");
  return match[1]!.trim().replace("cdn.<domain>", "cdn.arcavale.com");
}

describe("content security policy", () => {
  it("is ADR-0014's policy for arcavale.com", () => {
    expect(adrPolicy()).toBe("default-src 'self'; img-src 'self' https://cdn.arcavale.com; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
  });

  it("is delivered verbatim by _headers for every path", () => {
    const headers = inject("pagesHeaders");
    expect(headers).toMatch(/^\/\*$/m);
    const line = headers.split("\n").find((l) => l.trim().startsWith("Content-Security-Policy:"));
    expect(line?.trim().slice("Content-Security-Policy:".length).trim()).toBe(adrPolicy());
    for (const header of ["X-Content-Type-Options: nosniff", "X-Frame-Options: DENY", "Referrer-Policy: no-referrer", "Strict-Transport-Security:"]) expect(headers).toContain(header);
  });

  it("is repeated verbatim in the meta tag as a backup", () => {
    const doc = new MetaReader(inject("indexHtml"));
    expect(doc.meta("Content-Security-Policy")).toBe(adrPolicy());
  });

  it("the staging build differs only in the content host of img-src (ADR-0014, amended for V2-S4a)", () => {
    const staging = adrPolicy().replace("img-src 'self' https://cdn.arcavale.com;", "img-src 'self' https://cdn-staging.arcavale.com;");
    expect(staging).not.toBe(adrPolicy());
    const line = inject("stagingHeaders").split("\n").find((l) => l.trim().startsWith("Content-Security-Policy:"));
    expect(line?.trim().slice("Content-Security-Policy:".length).trim()).toBe(staging);
    expect(new MetaReader(inject("stagingIndexHtml")).meta("Content-Security-Policy")).toBe(staging);
    expect(inject("stagingHeaders").replace("cdn-staging.", "cdn.")).toBe(inject("pagesHeaders"));
    expect(inject("adr0014")).toContain("https://cdn-staging.arcavale.com");
    // No blob: or data: images, in either build.
    for (const text of [inject("pagesHeaders"), inject("stagingHeaders")]) expect(text).not.toMatch(/blob:|data:/);
  });

  it("index.html has no inline script, style or handler and loads only same-origin files", () => {
    const html = inject("indexHtml");
    const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
    expect(scripts).toHaveLength(1);
    expect(scripts[0]![1]).toContain('src="/assets/panel.js"');
    expect(scripts[0]![2]!.trim()).toBe("");
    expect(html).not.toMatch(/\son[a-z]+\s*=/i);
    expect(html).not.toMatch(/\sstyle\s*=/i);
    expect(html).not.toMatch(/<style[\s>]/i);
    for (const url of html.matchAll(/(?:src|href)="([^"]+)"/g)) expect(url[1]!.startsWith("/")).toBe(true);
  });
});

/** Reads one http-equiv meta value from the HTML text with a template element (inert: nothing runs or loads). */
class MetaReader {
  private readonly template: HTMLTemplateElement;

  constructor(html: string) {
    this.template = document.createElement("template");
    const head = /<head>([\s\S]*)<\/head>/i.exec(html)?.[1] ?? "";
    for (const tag of head.matchAll(/<meta\s+http-equiv="([^"]+)"\s+content="([^"]+)">/gi)) {
      const meta = document.createElement("meta");
      meta.setAttribute("http-equiv", tag[1]!);
      meta.setAttribute("content", tag[2]!);
      this.template.content.appendChild(meta);
    }
  }

  meta(name: string): string | null {
    return this.template.content.querySelector(`meta[http-equiv="${name}"]`)?.getAttribute("content") ?? null;
  }
}
