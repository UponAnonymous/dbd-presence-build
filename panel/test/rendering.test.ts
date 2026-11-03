// FR-ADM-04: user-controlled values are rendered as text only. Script-like strings in names, notes and audit values
// must appear verbatim and must never create elements, load anything or run.

import { describe, expect, it } from "vitest";
import { el, routeLink } from "../src/dom.ts";
import { HOSTILE, buttonByText, mount, settle } from "./fake.ts";

function assertInert(root: ParentNode): void {
  expect(root.querySelectorAll("img, script, iframe, object, embed, link, style").length).toBe(0);
  for (const node of root.querySelectorAll("*")) {
    for (const attr of node.getAttributeNames()) expect(attr.startsWith("on")).toBe(false);
  }
}

describe("text-only rendering", () => {
  it("shows a hostile invite note and user name as literal text on the Invites page", async () => {
    const { root } = await mount("invites");
    expect(root.textContent).toContain(HOSTILE);
    assertInert(root);
    // The redeemed invite names its user (whose display name is the hostile string) as text too.
    expect([...root.querySelectorAll("td")].filter((td) => td.textContent?.includes(HOSTILE)).length).toBeGreaterThanOrEqual(2);
  });

  it("shows a hostile display name as literal text on the Users page, in rows, devices and dialogs", async () => {
    const { root } = await mount("users");
    expect(root.textContent).toContain(HOSTILE);
    buttonByText(root, /1 active of 2/).click();
    await settle();
    expect(root.textContent).toContain(`Devices for ${HOSTILE}`);
    [...root.querySelectorAll("button")].find((b) => b.getAttribute("aria-label") === `Delete ${HOSTILE}`)!.click();
    await settle();
    expect(document.querySelector("dialog .dialog-title")?.textContent).toBe(`Delete ${HOSTILE}?`);
    assertInert(document.body);
  });

  it("shows hostile before/after values in the audit log as literal JSON text", async () => {
    const { root } = await mount("audit");
    const second = root.querySelectorAll<HTMLButtonElement>("button[data-entry]")[1]!;
    second.click();
    await settle();
    expect(root.querySelector("pre")?.textContent).toBe("None");
    expect(root.textContent).toContain(JSON.stringify({ banner: HOSTILE }, null, 2));
    assertInert(root);
  });

  it("el() puts strings into Text nodes and refuses handler, style and URL attributes", () => {
    const node = el("p", { text: HOSTILE }, HOSTILE);
    expect(node.childNodes.length).toBe(2);
    expect(node.querySelector("*")).toBeNull();
    for (const attr of ["onclick", "onerror", "style", "src", "srcset", "href", "action", "formaction", "xlink:href", ["src", "doc"].join("")]) {
      expect(() => el("div", { attrs: { [attr]: "x" } })).toThrow(/not allowed/);
    }
  });

  it("routeLink accepts only fixed in-page routes", () => {
    expect(routeLink("#/users", "Users").getAttribute("href")).toBe("#/users");
    expect(() => routeLink(["java", "script:alert(1)"].join(""), "x")).toThrow();
    expect(() => routeLink("https://example.com/#/users", "x")).toThrow();
  });
});
