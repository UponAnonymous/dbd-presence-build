// The character roster (PLAN_V2_S5B §2.1–2.3, owner decision D35). Pure code: the record, the signed payload, the
// name rules and the checks every change passes before it is stored. The rules are the app's own
// (src/DbdPresence.Packs/Roster/RosterParser.cs and Vocabulary.WithCharacters), so a roster the service signs is
// never refused by the app:
//   { "schemaVersion": 1,
//     "characters": [{ code, id, role, displayName, screenName, aliases: [{ name, seenOnScreen }], licensed, withdrawn }] }
// `licensed` is null or { "holder": "..." }. Characters are sorted by code. Every known character is listed, seed ones
// included; a removed character stays in the list with `withdrawn: true`, because the app never forgets a code or id.

import { foldName, skeleton } from "./fold.ts";

export type RosterRole = "killer" | "survivor";
export const ROSTER_ROLES: readonly RosterRole[] = ["killer", "survivor"];

/** Where a character came from: the reviewed seed, the owner's panel, or learned from the owner's game (D35). */
export type RosterSource = "seed" | "owner" | "auto";

export interface RosterAlias {
  name: string;
  seenOnScreen: string;
}

export interface RosterCharacter {
  code: string;
  id: string;
  role: RosterRole;
  displayName: string;
  screenName: string;
  aliases: RosterAlias[];
  licensedHolder: string | null;
  source: RosterSource;
  addedAt: number;
  withdrawnAt: number | null;
}

export interface RosterPayloadCharacter {
  code: string;
  id: string;
  role: RosterRole;
  displayName: string;
  screenName: string;
  aliases: RosterAlias[];
  licensed: { holder: string } | null;
  withdrawn: boolean;
}

export interface RosterPayload {
  schemaVersion: 1;
  characters: RosterPayloadCharacter[];
}

/** RosterParser caps (PLAN_V2_S5B §2.3). */
export const MAX_CHARACTERS = 400;
export const MAX_ALIASES = 8;
export const MAX_NAME_LENGTH = 80;
export const MAX_SEEN_ON_SCREEN_LENGTH = 200;
export const MAX_HOLDER_LENGTH = 120;
export const MAX_PAYLOAD_BYTES = 256 * 1024;

export const CODE_PATTERN = /^[KS]\d{2}$/;
export const ID_PATTERN = /^[a-z0-9-]{1,64}$/;
/** Pack slugs a character id may never take: `character:generic` is the generic tile (PackKey.GenericSlug in C#). */
export const RESERVED_IDS: ReadonlySet<string> = new Set(["generic"]);

/**
 * Letters (Basic Latin, Latin-1 Supplement without × and ÷, Latin Extended-A and -B, Latin Extended Additional), digits,
 * spaces and . ' " & : – - (PLAN_V2_S5B §2.2 check 1). Every character is in the Basic Multilingual Plane, so the C#
 * parser, which reads UTF-16 code units, applies exactly the same rule.
 */
const NAME_CHARACTERS = /^[A-Za-z0-9 .'"&:–À-ÖØ-öø-ɏḀ-ỿ-]+$/;
const CONTROL = /[\p{Cc}\p{Cf}]/u;

export function roleOfCode(code: string): RosterRole {
  return code.startsWith("K") ? "killer" : "survivor";
}

/** A display or screen name the app accepts: NFC, 1–80 characters, trimmed, single spaces, the allowed characters, and a non-empty fold. */
export function isValidName(text: string): boolean {
  if (text.length < 1 || text.length > MAX_NAME_LENGTH || text !== text.normalize("NFC")) return false;
  if (text !== text.trim() || text.includes("  ") || !NAME_CHARACTERS.test(text)) return false;
  const folded = foldName(text);
  return folded !== null && folded.length > 0;
}

/** Free text such as an alias's provenance or a rights holder: 1–max characters, trimmed, no control or format characters. */
export function isValidNote(text: string, max: number): boolean {
  return text.length >= 1 && text.length <= max && text === text.trim() && !CONTROL.test(text);
}

/** The on-screen name a display name implies: the part before the first " – " or " - " ("The Good Guy - Chucky" → "The Good Guy"). */
export function screenNameFrom(displayName: string): string {
  const cut = [" – ", " - "].map((sep) => displayName.indexOf(sep)).filter((i) => i > 0);
  return cut.length === 0 ? displayName : displayName.slice(0, Math.min(...cut));
}

/** The pack slug of a folded name: words joined by hyphens, at most 64 characters. */
export function slugFromFolded(folded: string): string {
  return folded.replaceAll(" ", "-").slice(0, 64).replace(/-+$/, "");
}

/** A readable name for a folded one: each word capitalised ("the lich" → "The Lich"). The owner can rename it. */
export function titleCase(folded: string): string {
  return folded
    .split(" ")
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(" ");
}

/** A free id for `wanted`: itself, or with -2, -3 … appended (never a reserved slug). */
export function freeId(wanted: string, taken: ReadonlySet<string>): string | null {
  for (let n = 1; n < 100; n++) {
    const suffix = n === 1 ? "" : `-${n}`;
    const candidate = `${wanted.slice(0, 64 - suffix.length).replace(/-+$/, "")}${suffix}`;
    if (ID_PATTERN.test(candidate) && !taken.has(candidate) && !RESERVED_IDS.has(candidate)) return candidate;
  }
  return null;
}

/**
 * The code a new character of `role` gets: one above the highest code of its letter, withdrawn ones included, because the
 * game numbers new characters in release order (INFERRED, PLAN_V2_S5B §0). Null past 99.
 */
export function nextCode(characters: readonly RosterCharacter[], role: RosterRole): string | null {
  const letter = role === "killer" ? "K" : "S";
  const highest = characters.filter((c) => c.code.startsWith(letter)).reduce((max, c) => Math.max(max, Number(c.code.slice(1))), 0);
  return highest >= 99 ? null : `${letter}${String(highest + 1).padStart(2, "0")}`;
}

/** Every spelling of a character: display name, screen name and aliases. */
export function spellings(c: Pick<RosterCharacter, "displayName" | "screenName" | "aliases">): string[] {
  return [c.displayName, c.screenName, ...c.aliases.map((a) => a.name)];
}

const byCode = (a: RosterCharacter, b: RosterCharacter): number => (a.code < b.code ? -1 : a.code > b.code ? 1 : 0);

export function rosterPayload(characters: readonly RosterCharacter[]): RosterPayload {
  return {
    schemaVersion: 1,
    characters: [...characters].sort(byCode).map((c) => ({
      code: c.code,
      id: c.id,
      role: c.role,
      displayName: c.displayName,
      screenName: c.screenName,
      aliases: c.aliases.map((a) => ({ name: a.name, seenOnScreen: a.seenOnScreen })),
      licensed: c.licensedHolder === null ? null : { holder: c.licensedHolder },
      withdrawn: c.withdrawnAt !== null,
    })),
  };
}

/**
 * The first rule the roster breaks, or null. The app's parser repeats the shape rules; the skeleton rule is the one its
 * vocabulary applies when it merges the roster (a collision there would make the app refuse the whole document).
 */
export function rosterProblem(characters: readonly RosterCharacter[]): string | null {
  if (characters.length > MAX_CHARACTERS) return "too_many_characters";
  const codes = new Set<string>();
  const ids = new Set<string>();
  const skeletons = new Map<string, string>();
  for (const c of characters) {
    if (!CODE_PATTERN.test(c.code) || codes.has(c.code) || roleOfCode(c.code) !== c.role) return "bad_code";
    codes.add(c.code);
    if (!ID_PATTERN.test(c.id) || RESERVED_IDS.has(c.id) || ids.has(c.id)) return "bad_id";
    ids.add(c.id);
    if (!isValidName(c.displayName) || !isValidName(c.screenName)) return "bad_name";
    if (c.aliases.length > MAX_ALIASES) return "bad_alias";
    for (const alias of c.aliases) if (!isValidName(alias.name) || !isValidNote(alias.seenOnScreen, MAX_SEEN_ON_SCREEN_LENGTH)) return "bad_alias";
    if (c.licensedHolder !== null && !isValidNote(c.licensedHolder, MAX_HOLDER_LENGTH)) return "bad_holder";
    for (const spelling of spellings(c)) {
      const key = skeleton(spelling)!;
      const owner = skeletons.get(key);
      if (owner !== undefined && owner !== c.code) return "name_collision";
      skeletons.set(key, c.code);
    }
  }
  if (new TextEncoder().encode(JSON.stringify(rosterPayload(characters))).length > MAX_PAYLOAD_BYTES) return "too_large";
  return null;
}

/** True when `after` keeps every character of `before` with the same id and role (the roster never shrinks). */
export function keepsEveryCharacter(before: readonly RosterCharacter[], after: readonly RosterCharacter[]): boolean {
  const next = new Map(after.map((c) => [c.code, c]));
  return before.every((c) => {
    const kept = next.get(c.code);
    return kept !== undefined && kept.id === c.id && kept.role === c.role && (c.source !== "seed" || kept.withdrawnAt === null);
  });
}

/** The fewest letters in a learned character name (the app's CharacterNameShape.MinLetters). */
export const MIN_LEARNED_LETTERS = 5;

/**
 * True when an already folded name has the shape of a character name, as the app's CharacterNameShape requires before it
 * reports one: two to five words of letters only (no digit), at least two of them of two letters or more (a one-letter
 * word is an initial), at least five letters in all. A player name with a digit, or a single word, is refused here too,
 * so a modified client cannot get one into the roster (round-2 review M1c).
 */
export function isCharacterShaped(folded: string): boolean {
  if (!/^[a-z]+( [a-z]+)*$/.test(folded)) return false;
  const words = folded.split(" ");
  if (words.length < 2 || words.length > 5) return false;
  if (words.filter((w) => w.length >= 2).length < 2) return false;
  return words.join("").length >= MIN_LEARNED_LETTERS;
}
