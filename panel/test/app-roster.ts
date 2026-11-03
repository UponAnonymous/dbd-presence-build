// The roster the service starts with, built the way service/scripts/make-roster-migration.mjs builds it: every code
// and id of tests/fixtures/roster/seed.json with the vocabulary's names and aliases. The panel's slot list and file
// matching are tested against it, so they cover every character the app embeds.

import { inject } from "vitest";
import type { RosterCharacter } from "../src/api.ts";

interface VocabularyCharacter {
  id: string;
  code: string;
  name: string;
  aliases?: { name: string; seenOnScreen: string }[];
}

export interface Vocabulary {
  killers: VocabularyCharacter[];
  survivors: VocabularyCharacter[];
}

export const vocabulary = (): Vocabulary => JSON.parse(inject("vocabulary")) as Vocabulary;

export function appRoster(): RosterCharacter[] {
  const seed = JSON.parse(inject("rosterSeed")) as { characters: { code: string; id: string; role: "killer" | "survivor" }[] };
  const v = vocabulary();
  const byId = new Map([...v.killers, ...v.survivors].map((e) => [e.id, e]));
  return seed.characters.map((c) => {
    const entry = byId.get(c.id);
    if (!entry || entry.code !== c.code) throw new Error(`seed ${c.code} ${c.id} is not in the vocabulary with that code`);
    return {
      code: c.code,
      id: c.id,
      role: c.role,
      displayName: entry.name,
      screenName: entry.name,
      aliases: (entry.aliases ?? []).map((a) => ({ name: a.name, seenOnScreen: a.seenOnScreen })),
      licensedHolder: null,
      source: "seed",
      addedAt: null,
      withdrawn: false,
      withdrawnAt: null,
    };
  });
}
