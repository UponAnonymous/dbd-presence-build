// Matches a picture's file name to a pack slot for "Add pictures from a folder" (FR-ADM-08; PLAN_V2_S5B §2.5, §2.7).
// The rules are the app's import rules (src/DbdPresence.Packs/Import/MappingProposer.cs), so a folder named for the app
// matches the same way here, plus a few spellings people use for file names. In order:
//   1. the base name loses a leading `T_UI_` and a trailing `_Portrait` (any case);
//   2. a roster code alone (`K27.png`) or in front (`K27_TheOnryo.png`) names that character; when the rest of the
//      name is another character's name, nothing is chosen (code and name disagree);
//   3. the name, folded (case, spaces, punctuation and accents removed), equals one slot's name, other name or slug;
//      a leading "The" may be left out or added;
//   4. the same after common extras are dropped: a title (Dr., Detective), a nickname in quotes or brackets (either
//      kept in place of the word before it, or left out), trailing words such as "portrait" or "icon", and copy
//      numbers such as "(1)"; then with the last word moved to the front or the first word to the end;
//   5. a character id, map or role slug contained in the name (the app's last rule).
// A step that finds two slots chooses nothing, so the owner decides. The panel only proposes: every match is shown in
// the review and the owner can change it before anything is uploaded.

import type { Slot, SlotCatalog } from "./catalog.ts";
import { ROLE_SLOTS, MAP_SLOTS, slugify } from "./catalog.ts";

/**
 * Other names of characters that people use for portrait files: the killers' own names and a few longer forms. They help
 * match file names only; they are never sent anywhere and never change the roster.
 */
export const FILE_NAME_HINTS: Readonly<Record<string, readonly string[]>> = {
  "the-trapper": ["Evan MacMillan"],
  "the-wraith": ["Philip Ojomo"],
  "the-hillbilly": ["Max Thompson Jr", "Max Thompson"],
  "the-nurse": ["Sally Smithson"],
  "the-shape": ["Michael Myers"],
  "the-hag": ["Lisa Sherwood"],
  "the-doctor": ["Herman Carter"],
  "the-huntress": ["Anna"],
  "the-cannibal": ["Bubba Sawyer", "Leatherface"],
  "the-nightmare": ["Freddy Krueger"],
  "the-pig": ["Amanda Young"],
  "the-clown": ["Kenneth Chase", "Jeffrey Hawk"],
  "the-spirit": ["Rin Yamaoka"],
  "the-legion": ["Frank Morrison"],
  "the-plague": ["Adiris"],
  "the-ghost-face": ["Danny Johnson", "Ghostface"],
  "the-oni": ["Kazan Yamaoka"],
  "the-deathslinger": ["Caleb Quinn"],
  "the-executioner": ["Pyramid Head"],
  "the-blight": ["Talbot Grimes"],
  "the-twins": ["Charlotte & Victor Deshayes", "Victor & Charlotte Deshayes"],
  "the-trickster": ["Ji-Woon Hak"],
  "the-nemesis": ["Nemesis T-Type"],
  "the-cenobite": ["Pinhead"],
  "the-artist": ["Carmina Mora"],
  "the-onryo": ["Sadako Yamamura", "Sadako"],
  "the-mastermind": ["Albert Wesker", "Wesker"],
  "the-knight": ["Tarhos Kovács"],
  "the-skull-merchant": ["Adriana Imai"],
  "the-singularity": ["HUX-A7"],
  "the-good-guy": ["Chucky", "Charles Lee Ray"],
  "the-lich": ["Vecna"],
  "the-dark-lord": ["Dracula"],
  "the-houndmaster": ["Portia Maye"],
  "the-ghoul": ["Ken Kaneki"],
  "the-animatronic": ["Springtrap"],
  "bill-overbeck": ["William Overbeck", "William Bill Overbeck"],
  "ash-williams": ["Ashley Williams", "Ashley J. Williams"],
  "david-tapp": ["Detective Tapp"],
};

/** How a file was matched, for the review's status column. */
export type MatchBasis = "code" | "name" | "similar" | "partial";

export type FileMatch =
  | { status: "matched"; key: string; basis: MatchBasis }
  /** The code names `key`, the rest of the name names `other`. */
  | { status: "conflict"; key: string; other: string }
  /** More than one slot fits the name. */
  | { status: "ambiguous"; keys: string[] }
  | { status: "unmatched" };

const MARK = /\p{M}/u;
const LETTER_OR_DIGIT = /[\p{L}\p{Nd}]/u;

/** Letters and digits only, lower case, accents removed ("The Onryō" and "the_onryo" both give "theonryo"). */
export function fold(text: string): string {
  let out = "";
  for (const c of text.normalize("NFKD")) {
    if (MARK.test(c)) continue;
    if (LETTER_OR_DIGIT.test(c)) out += c.toLowerCase();
  }
  return out;
}

/** The words of a name: split at separators and at a change from lower to upper case ("TheOnryo" → the, onryo). */
function words(text: string): string[] {
  return text
    .normalize("NFKD")
    .replace(/\p{M}/gu, "")
    .replace(/(\p{Ll})(\p{Lu})/gu, "$1 $2")
    .split(/[^\p{L}\p{Nd}]+/u)
    .filter((w) => w.length > 0)
    .map((w) => w.toLowerCase());
}

const CODE = /^[KS]\d{2}$/;
const TITLES = new Set(["dr", "mr", "mrs", "ms", "miss", "detective"]);
const TRAILING = new Set(["portrait", "portraits", "icon", "image", "picture", "pic", "art", "copy", "final", "new", "hd"]);
/** A nickname in quotes or brackets, as in "Jeffrey 'Jeff' Johansen" or "Ji-Woon (Jiwoon) Hak". */
const NICKNAME = /(^|[\s_-])['"‘“(\[]([^'"’”)\]]+)['"’”)\]](?=$|[\s_-])/u;

/** The base name without extension, `T_UI_` and `_Portrait`, as the app strips them. */
export function stemOf(fileName: string): string {
  const base = fileName.slice(fileName.lastIndexOf("/") + 1);
  const dot = base.lastIndexOf(".");
  let stem = dot > 0 ? base.slice(0, dot) : base;
  if (/^t_ui_/i.test(stem)) stem = stem.slice(5);
  if (/_portrait$/i.test(stem)) stem = stem.slice(0, -"_portrait".length);
  return stem.trim();
}

/** The spellings a file name may use for a slot, folded. */
function spellings(slot: Slot): string[] {
  const colon = slot.key.indexOf(":");
  const slug = slot.key.slice(colon + 1);
  const names = [slot.label, ...slot.otherNames, ...(slot.code ? (FILE_NAME_HINTS[slug] ?? []) : [])];
  const out = new Set<string>();
  for (const name of names) {
    out.add(fold(name));
    if (name.includes("&")) out.add(fold(name.replaceAll("&", " and ")));
    for (const dash of [" – ", " - "]) {
      const at = name.indexOf(dash);
      if (at > 0) out.add(fold(name.slice(0, at)));
    }
  }
  out.add(fold(slug));
  if (!slot.code) out.add(fold(slot.key));
  out.delete("");
  return [...out];
}

/** The folded forms step 4 tries for a stem. */
function variants(stem: string): { similar: string[]; reordered: string[] } {
  const texts = [stem];
  const nick = NICKNAME.exec(stem);
  if (nick) {
    const before = stem.slice(0, nick.index + nick[1]!.length);
    const after = stem.slice(nick.index + nick[0].length);
    texts.push(`${before} ${after}`);
    const lastWord = /[\p{L}\p{Nd}]+[\s_-]*$/u.exec(before);
    if (lastWord) texts.push(`${before.slice(0, lastWord.index)}${nick[2]} ${after}`);
    else texts.push(`${nick[2]} ${after}`);
  }
  const similar = new Set<string>();
  const reordered = new Set<string>();
  for (const text of texts) {
    let w = words(text);
    while (w.length > 1 && TITLES.has(w[0]!)) w = w.slice(1);
    while (w.length > 1 && (TRAILING.has(w[w.length - 1]!) || /^\d{1,3}$/.test(w[w.length - 1]!) || /^v\d{1,3}$/.test(w[w.length - 1]!))) w = w.slice(0, -1);
    similar.add(w.join(""));
    if (w.length > 1) {
      reordered.add([w[w.length - 1]!, ...w.slice(0, -1)].join(""));
      reordered.add([...w.slice(1), w[0]!].join(""));
    }
  }
  similar.delete("");
  return { similar: [...similar], reordered: [...reordered] };
}

const same = (spelling: string, folded: string) =>
  spelling === folded || (spelling.startsWith("the") && spelling.slice(3) === folded) || (folded.startsWith("the") && folded.slice(3) === spelling);

/** The keys whose spellings equal any of the folded forms. */
function byName(table: readonly { key: string; spellings: string[] }[], forms: readonly string[]): string[] {
  const keys = new Set<string>();
  for (const form of forms) for (const row of table) if (row.spellings.some((s) => same(s, form))) keys.add(row.key);
  return [...keys];
}

const contains = (padded: string, slug: string) => padded.includes(`-${slug}-`) || (slug.startsWith("the-") && padded.includes(`-${slug.slice(4)}-`));

/** A matcher for one catalogue: build it once per review, then match each file. */
export class FileMatcher {
  private readonly table: { key: string; spellings: string[] }[];
  private readonly byCode: ReadonlyMap<string, string>;
  private readonly characterIds: string[];

  constructor(catalog: SlotCatalog) {
    const slots = catalog.slots;
    this.table = slots.map((s) => ({ key: s.key, spellings: spellings(s) }));
    this.byCode = new Map(slots.filter((s) => s.code !== null).map((s) => [s.code!, s.key]));
    this.characterIds = slots.filter((s) => s.code !== null).map((s) => s.key.slice("character:".length));
  }

  match(fileName: string): FileMatch {
    const stem = stemOf(fileName);
    if (stem.length === 0) return { status: "unmatched" };
    const upper = stem.toUpperCase();
    if (CODE.test(upper)) {
      const key = this.byCode.get(upper);
      return key ? { status: "matched", key, basis: "code" } : { status: "unmatched" };
    }
    const coded = /^([KSks]\d{2})[\s_-]+(.+)$/u.exec(stem);
    const codeKey = coded ? this.byCode.get(coded[1]!.toUpperCase()) : undefined;
    const rest = coded && codeKey ? coded[2]! : stem;
    const named = this.byNameSteps(rest);
    if (codeKey) {
      if (named.status === "matched" && named.key !== codeKey) return { status: "conflict", key: codeKey, other: named.key };
      return { status: "matched", key: codeKey, basis: "code" };
    }
    return named;
  }

  private byNameSteps(rest: string): FileMatch {
    const exact = byName(this.table, [fold(rest)]);
    if (exact.length > 0) return this.decide(exact, "name");
    const { similar, reordered } = variants(rest);
    const near = byName(this.table, similar);
    if (near.length > 0) return this.decide(near, "similar");
    const swapped = byName(this.table, reordered);
    if (swapped.length > 0) return this.decide(swapped, "similar");
    return this.partial(slugify(rest));
  }

  private decide(keys: string[], basis: MatchBasis): FileMatch {
    return keys.length === 1 ? { status: "matched", key: keys[0]!, basis } : { status: "ambiguous", keys };
  }

  /** The app's MatchBySlug: a character id, then a map, then a role slug inside the name. */
  private partial(slug: string): FileMatch {
    if (slug.length === 0) return { status: "unmatched" };
    const padded = `-${slug}-`;
    const ids = this.characterIds.filter((id) => contains(padded, id));
    if (ids.length > 1) return { status: "ambiguous", keys: ids.map((id) => `character:${id}`) };
    if (ids.length === 1) return { status: "matched", key: `character:${ids[0]}`, basis: "partial" };
    for (const group of [MAP_SLOTS, ROLE_SLOTS]) {
      const hit = group.find((s) => contains(padded, s.key.slice(s.key.indexOf(":") + 1)));
      if (hit) return { status: "matched", key: hit.key, basis: "partial" };
    }
    return { status: "unmatched" };
  }
}
