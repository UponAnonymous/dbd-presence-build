// The image slots an owner pack can fill (FR-PACK-06, FR-ADM-13; PLAN_V2_S5B §2.5): mapping keys `kind:slug` exactly as
// the desktop app looks them up. Killers and survivors come from the signed character roster the service holds (GET
// /api/roster): the app's pictures use `character:<roster id>` (src/DbdPresence.Core/Pictures/PictureModel.cs
// PictureKeys), and the roster starts with every character the app embeds (the vocabulary) plus the ones added since.
// The other groups are the app's fixed keys:
//   roles            src/DbdPresence.Packs/Import/KnownSlugs.cs Roles
//   generic tiles    src/DbdPresence.Presentation/ViewModels/PackItemViewModels.cs ForKey
//   status, result   src/DbdPresence.Core/Composition/AutomaticIcons.cs AllSlugs (FR-AUTO-02, owner packs only)
//   maps             src/DbdPresence.Packs/Import/KnownSlugs.cs Maps
// test/catalog.test.ts compares each list with those files and the roster built from the vocabulary, so the panel never
// offers fewer slots than the app uses. Anything else is entered as a custom name and slugged with the app's rule.

import type { EntryKind, RosterCharacter, RosterRole } from "./api.ts";
import { knownLicensedHolder } from "./licensed.ts";

export interface Slot {
  key: string;
  label: string;
  /** The roster code (`K27`) of a character slot; null for every other slot. */
  code: string | null;
  role: RosterRole | null;
  /** Other names a file may carry for this slot: the screen name, aliases and the slug. */
  otherNames: readonly string[];
  /** The rights holder of a licensed slot (the roster's, else licensed.ts), or null. */
  licensedHolder: string | null;
}

export interface SlotGroup {
  label: string;
  slots: readonly Slot[];
}

/** The app's slug rule: ASCII letters and digits, lower-cased; every other run of characters becomes one hyphen. */
export function slugify(value: string): string {
  let out = "";
  let pendingHyphen = false;
  for (const c of value) {
    if (/^[A-Za-z0-9]$/.test(c)) {
      if (pendingHyphen && out.length > 0) out += "-";
      out += c.toLowerCase();
      pendingHyphen = false;
    } else {
      pendingHyphen = true;
    }
  }
  return out;
}

export const KEY_PATTERN = /^(role|character|map|app):[a-z0-9-]{1,64}$/;

const fixed = (key: string, label: string, ...otherNames: string[]): Slot => ({ key, label, code: null, role: null, otherNames, licensedHolder: knownLicensedHolder(key) });

/** KnownSlugs.Roles. */
export const ROLE_SLOTS: readonly Slot[] = [fixed("role:killer", "Killer icon", "killer"), fixed("role:survivor", "Survivor icon", "survivor")];

/** The fixed tiles PackItemViewModels.ForKey names. */
export const GENERIC_SLOTS: readonly Slot[] = [
  fixed("character:generic-killer", "Generic killer portrait", "generic killer"),
  fixed("character:generic-survivor", "Generic survivor portrait", "generic survivor"),
  fixed("map:generic", "Generic map tile", "generic map"),
  fixed("app:mode-generic", "Game mode tile", "mode generic"),
  fixed("app:mark", "App mark", "app logo", "logo"),
];

/** AutomaticIcons.AllSlugs: the small picture the app shows for the owner's own state and the match result. */
export const AUTOMATIC_SLOTS: readonly Slot[] = [
  fixed("app:status-hooked", "On a hook", "hooked"),
  fixed("app:status-dying", "Down (dying)", "dying"),
  fixed("app:result-escaped", "Result: Escaped", "escaped"),
  fixed("app:result-sacrificed", "Result: Sacrificed", "sacrificed"),
  fixed("app:result-dead", "Result: Killed", "killed", "dead"),
  fixed("app:result-entity-hungers", "Result: Entity Hungers", "entity hungers"),
  fixed("app:result-brutal", "Result: Brutal Killer", "brutal killer", "brutal"),
  fixed("app:result-ruthless", "Result: Ruthless Killer", "ruthless killer", "ruthless"),
  fixed("app:result-merciless", "Result: Merciless Killer", "merciless killer", "merciless"),
];

/** KnownSlugs.Maps: the map slugs the app proposes from file names. */
export const MAP_SLOTS: readonly Slot[] = [
  fixed("map:autohaven-wreckers", "Autohaven Wreckers"),
  fixed("map:coldwind-farm", "Coldwind Farm"),
  fixed("map:crotus-prenn-asylum", "Crotus Prenn Asylum"),
  fixed("map:haddonfield", "Haddonfield"),
  fixed("map:hawkins", "Hawkins National Laboratory", "hawkins"),
  fixed("map:red-forest", "Red Forest"),
  fixed("map:springwood", "Springwood"),
  fixed("map:the-game", "The Game"),
  fixed("map:yamaoka-estate", "Yamaoka Estate"),
];

const KIND_WORD: Readonly<Record<string, string>> = { role: "Role", character: "Character", map: "Map", app: "App" };

function characterSlot(c: RosterCharacter): Slot {
  const otherNames = [c.screenName, ...c.aliases.map((a) => a.name)].filter((n) => n !== c.displayName);
  const key = `character:${c.id}`;
  // The roster's rights holder wins; otherwise the panel's list of licensed characters (licensed.ts) fills it in.
  return { key, label: c.displayName, code: c.code, role: c.role, otherNames: [...new Set(otherNames)], licensedHolder: c.licensedHolder ?? knownLicensedHolder(key) };
}

/**
 * Every slot the app uses, with the roster's characters. `characters` null means the roster could not be loaded: the
 * killer and survivor groups are then empty and the editor says so.
 */
export class SlotCatalog {
  readonly groups: readonly SlotGroup[];
  readonly rosterLoaded: boolean;
  private readonly byKey: ReadonlyMap<string, Slot>;

  constructor(characters: readonly RosterCharacter[] | null) {
    this.rosterLoaded = characters !== null;
    const all = [...(characters ?? [])].sort((a, b) => (a.code < b.code ? -1 : a.code > b.code ? 1 : 0));
    const current = all.filter((c) => !c.withdrawn);
    this.groups = [
      { label: "Roles", slots: ROLE_SLOTS },
      { label: "Generic tiles", slots: GENERIC_SLOTS },
      { label: "Killers", slots: current.filter((c) => c.role === "killer").map(characterSlot) },
      { label: "Survivors", slots: current.filter((c) => c.role === "survivor").map(characterSlot) },
      { label: "Status and result pictures", slots: AUTOMATIC_SLOTS },
      { label: "Maps", slots: MAP_SLOTS },
    ];
    // Withdrawn characters keep their name for entries a pack still has, but are not offered as slots.
    const known = new Map<string, Slot>();
    for (const c of all) known.set(`character:${c.id}`, characterSlot(c));
    for (const g of this.groups) for (const s of g.slots) known.set(s.key, s);
    this.byKey = known;
  }

  /** Every slot offered, in group order. */
  get slots(): Slot[] {
    return this.groups.flatMap((g) => g.slots);
  }

  find(key: string): Slot | undefined {
    return this.byKey.get(key);
  }

  /** True when the key is one the panel lists (a withdrawn character's key included). */
  isKnown(key: string): boolean {
    return this.byKey.has(key);
  }

  /** A readable name for a key: the known label, or the slug with its kind. */
  label(key: string): string {
    const known = this.byKey.get(key);
    if (known) return known.label;
    const colon = key.indexOf(":");
    return `${KIND_WORD[key.slice(0, colon)] ?? "App"}: ${key.slice(colon + 1)}`;
  }
}

export const KIND_LABELS: Readonly<Record<EntryKind, string>> = { role: "Role", character: "Character", map: "Map", app: "App" };
