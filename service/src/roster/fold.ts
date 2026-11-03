// Character-name folding shared with the desktop app (PLAN_V2_S5B §2.2 check 4, D35). Pure code.
//
// foldName is the app's own fold (src/DbdPresence.Core/Roster/RosterNameFold.cs, which follows the OCR normaliser
// src/DbdPresence.Vision/Text/TextNormalizer.cs Normalize): Unicode compatibility decomposition, combining marks
// dropped, ø/ł/đ/ß folded, apostrophes removed, lower case, every other character a single space. A name whose letters
// or digits do not all fold to a-z and 0-9 has no fold (null). The app sends only this folded form when it reports an
// unknown character, and "the identical folded name" of D35 is equality of this text.
//
// skeleton is TextNormalizer.Skeleton on top of the fold: the characters OCR confuses (l/1/i, O/0, S/5, B/8, Z/2, rn/m,
// vv/w) are folded together. The app's vocabulary refuses two characters whose names share a skeleton, so the service
// checks the same before anything reaches the signed roster. Both functions are checked against the shared fixture
// tests/fixtures/roster/fold-cases.json in this suite and in the C# Core and Vision suites.

const SPECIAL: Readonly<Record<string, string>> = { "ø": "o", "Ø": "o", "ł": "l", "Ł": "l", "đ": "d", "Đ": "d", "ß": "s" };
const APOSTROPHES = new Set(["'", "‘", "’", "`", "´", "ʼ"]);
const MARK = /\p{M}/u;
const LETTER_OR_DIGIT = /[\p{L}\p{Nd}]/u;
const ASCII_ALNUM = /^[a-z0-9]$/;

/** The fold of a name, or null when one of its letters or digits does not fold to a-z or 0-9. */
export function foldName(text: string): string | null {
  let out = "";
  let pendingSpace = false;
  for (const raw of text.normalize("NFKD")) {
    if (MARK.test(raw)) continue;
    const c = SPECIAL[raw] ?? raw;
    if (APOSTROPHES.has(c)) continue;
    if (LETTER_OR_DIGIT.test(c)) {
      const lower = c.toLowerCase();
      if (!ASCII_ALNUM.test(lower)) return null;
      if (pendingSpace && out.length > 0) out += " ";
      pendingSpace = false;
      out += lower;
    } else {
      pendingSpace = true;
    }
  }
  return out;
}

const CONFUSABLE: Readonly<Record<string, string>> = { "1": "l", i: "l", l: "l", "0": "o", "5": "s", "8": "b", "2": "z" };

/** The OCR skeleton of an already folded name (TextNormalizer.Skeleton). */
export function skeletonOfFolded(folded: string): string {
  let out = "";
  for (const c of folded) out += CONFUSABLE[c] ?? c;
  return out.replaceAll("rn", "m").replaceAll("vv", "w");
}

/** The OCR skeleton of a name, or null when it has no fold. */
export function skeleton(text: string): string | null {
  const folded = foldName(text);
  return folded === null ? null : skeletonOfFolded(folded);
}

/** A folded name as the app reports it: 2 to 40 characters, words of a-z and 0-9 separated by single spaces. */
export const FOLDED_NAME_PATTERN = /^(?=.{2,40}$)[a-z0-9]+( [a-z0-9]+)*$/;
