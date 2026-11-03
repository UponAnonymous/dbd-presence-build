// FR-ADM-13 / PLAN_V2_S5B §2.5: the pack editor offers a slot for every character the app knows, under the key the app
// looks up (`character:<roster id>`), and every fixed slot the app uses (roles, generic tiles, the nine automatic
// pictures of FR-AUTO-02, the map slugs). Each list is compared with the app's own source file.

import { describe, expect, inject, it } from "vitest";
import { AUTOMATIC_SLOTS, GENERIC_SLOTS, MAP_SLOTS, ROLE_SLOTS, SlotCatalog } from "../src/catalog.ts";
import { knownLicensedKeys } from "../src/licensed.ts";
import { appRoster, vocabulary } from "./app-roster.ts";
import { FakeBackend, buttonByLabel, buttonByText, mount, sampleEntry, sampleRoster, settle } from "./fake.ts";

/** The string literals of one C# list initialiser `Name = [ ... ];` or `Name { get; } = [ ... ];`. */
function csList(source: string, name: string): string[] {
  const match = new RegExp(`${name}[^=]*=\\s*\\[([^\\]]*)\\]`).exec(source);
  if (!match) throw new Error(`${name} not found`);
  return [...match[1]!.matchAll(/"([^"]*)"/g)].map((m) => m[1]!);
}

const slugs = (slots: readonly { key: string }[]) => slots.map((s) => s.key.slice(s.key.indexOf(":") + 1));

describe("the slot list is the app's", () => {
  const catalog = new SlotCatalog(appRoster());
  const group = (label: string) => catalog.groups.find((g) => g.label === label)!.slots;

  it("lists every killer and survivor of the app's vocabulary, by its roster id, in code order", () => {
    const v = vocabulary();
    const byCode = (a: { code: string }, b: { code: string }) => (a.code < b.code ? -1 : 1);
    v.killers.sort(byCode);
    v.survivors.sort(byCode);
    expect(group("Killers").map((s) => s.key)).toEqual(v.killers.map((k) => `character:${k.id}`));
    expect(group("Survivors").map((s) => s.key)).toEqual(v.survivors.map((k) => `character:${k.id}`));
    expect(group("Killers")).toHaveLength(44);
    expect(group("Survivors")).toHaveLength(54);
    expect(group("Killers").map((s) => s.label)).toEqual(v.killers.map((k) => k.name));
    // The keys the old hand-written list got wrong or left out.
    for (const key of ["character:the-ghost-face", "character:the-ghoul", "character:the-krasue", "character:the-judgment", "character:elodie-rakoto", "character:aestri-yazar-baermar-uraz", "character:aurora-stardotter"]) {
      expect(catalog.isKnown(key)).toBe(true);
    }
    expect(catalog.isKnown("character:ghostface")).toBe(false);
  });

  it("offers the role slugs of KnownSlugs.Roles and the map slugs of KnownSlugs.Maps", () => {
    const source = inject("knownSlugs");
    expect(slugs(ROLE_SLOTS)).toEqual(csList(source, "Roles"));
    expect(new Set(slugs(MAP_SLOTS))).toEqual(new Set(csList(source, "Maps")));
    expect(MAP_SLOTS.every((s) => s.key.startsWith("map:"))).toBe(true);
  });

  it("offers the nine reserved automatic pictures of AutomaticIcons (FR-AUTO-02)", () => {
    const source = inject("automaticIcons");
    const constants = new Map([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)].map((m) => [m[1]!, m[2]!]));
    const all = /AllSlugs \{ get; \} =\s*\[([^\]]*)\]/.exec(source)![1]!.split(",").map((n) => constants.get(n.trim())!);
    expect(all).toHaveLength(9);
    expect(AUTOMATIC_SLOTS.map((s) => s.key)).toEqual(all.map((slug) => `app:${slug}`));
  });

  it("offers every fixed key the app names (PackItemViewModels.ForKey)", () => {
    const fixed = [...inject("packItemViewModels").matchAll(/^\s*"((?:role|character|map|app):[a-z0-9-]+)" =>/gm)].map((m) => m[1]!);
    expect(fixed.length).toBeGreaterThanOrEqual(7);
    expect(new Set([...ROLE_SLOTS, ...GENERIC_SLOTS].map((s) => s.key))).toEqual(new Set(fixed));
  });

  it("every key passes the service's key rule, and no key is offered twice", () => {
    const keys = catalog.slots.map((s) => s.key);
    for (const key of keys) expect(key).toMatch(/^(role|character|map|app):[a-z0-9-]{1,64}$/);
    expect(new Set(keys).size).toBe(keys.length);
  });

  it("adds the characters the owner or the game added, leaves withdrawn ones out of the list but keeps their names", () => {
    const roster = sampleRoster().roster.characters;
    const withRoster = new SlotCatalog(roster);
    const listed = withRoster.slots.map((s) => s.key);
    expect(listed).toContain("character:the-lantern");
    expect(listed).not.toContain("character:mara-quell");
    expect(withRoster.label("character:mara-quell")).toBe("Mara Quell");
    expect(withRoster.find("character:the-shape")?.licensedHolder).toBe("Example Studio");
  });

  it("fills in the rights holder of a known licensed character or map when the roster has none; the roster's own wins", () => {
    const withRoster = new SlotCatalog(sampleRoster().roster.characters);
    expect(withRoster.find("character:the-ghost-face")?.licensedHolder).toBe("Fun World (Ghost Face)");
    expect(withRoster.find("character:the-shape")?.licensedHolder).toBe("Example Studio");
    expect(withRoster.find("character:the-trapper")?.licensedHolder).toBeNull();
    expect(withRoster.find("map:haddonfield")?.licensedHolder).toBe("Halloween");
    expect(withRoster.find("map:coldwind-farm")?.licensedHolder).toBeNull();
    expect(new SlotCatalog(null).find("map:hawkins")?.licensedHolder).toBe("Stranger Things");
  });

  it("names only real slots in the list of licensed characters and maps", () => {
    const keys = knownLicensedKeys();
    expect(keys.length).toBeGreaterThan(40);
    const known = new Set([...catalog.slots.map((s) => s.key), "character:the-terrifier", "character:sienna-shaw"]);
    expect(keys.filter((k) => !known.has(k))).toEqual([]);
  });

  it("without the roster offers only the fixed slots and names keys by kind", () => {
    const empty = new SlotCatalog(null);
    expect(empty.rosterLoaded).toBe(false);
    expect(empty.groups.find((g) => g.label === "Killers")!.slots).toHaveLength(0);
    expect(empty.label("character:the-trapper")).toBe("Character: the-trapper");
  });
});

describe("the pack editor's slot list", () => {
  it("offers one slot for each character of the roster the service serves, and none for a withdrawn one", async () => {
    const { root, backend } = await mount("packs");
    buttonByLabel(root, "Edit Original").click();
    await settle();
    const values = [...root.querySelectorAll<HTMLOptionElement>("#add-image-slot option")].map((o) => o.value);
    const current = backend.roster.roster.characters.filter((c) => !c.withdrawn).map((c) => `character:${c.id}`);
    for (const key of current) expect(values).toContain(key);
    expect(values).not.toContain("character:mara-quell");
    expect(values.filter((v) => v.startsWith("character:") && !v.startsWith("character:generic-"))).toHaveLength(current.length);
  });

  it("says when the character list could not be loaded, and loads it on Try again", async () => {
    const backend = new FakeBackend();
    backend.failOnce("GET /api/roster", { status: 503 });
    const { root } = await mount("packs", { backend });
    buttonByLabel(root, "Edit Original").click();
    await settle();
    expect(root.textContent).toContain("The character list could not be loaded");
    expect([...root.querySelectorAll<HTMLOptionElement>("#add-image-slot option")].some((o) => o.value === "character:the-hag")).toBe(false);
    buttonByText(root, "Try again").click();
    await settle();
    expect(root.textContent).not.toContain("The character list could not be loaded");
    expect([...root.querySelectorAll<HTMLOptionElement>("#add-image-slot option")].some((o) => o.value === "character:the-hag")).toBe(true);
  });

  it("marks an entry whose character key no character has", async () => {
    const backend = new FakeBackend();
    backend.data.packs[0]!.entries.push(sampleEntry(9, "character:ghostface"));
    const { root } = await mount("packs", { backend });
    buttonByLabel(root, "Edit Original").click();
    await settle();
    expect(root.textContent).toContain("No character has this slot");
  });
});
