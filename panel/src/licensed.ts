// Characters and maps owned by someone other than Behaviour (owner decision D14; FR-PACK-04): the panel ticks "Licensed
// third-party content" for these slots and fills in the name below, so the owner does not have to remember which ones
// are licensed. It is only a starting value: the owner checks it before uploading and can change it, and a rights holder
// the roster records (Characters page) always wins. The names are the series or owner each character or map is licensed
// from, not a company's legal name. Keys are the app's slot keys (catalog.ts). Characters added later and not listed
// here start unticked, as before.

const KNOWN: Readonly<Record<string, string>> = {
  // Killers
  "character:the-shape": "Halloween",
  "character:the-cannibal": "The Texas Chain Saw Massacre",
  "character:the-nightmare": "A Nightmare on Elm Street",
  "character:the-pig": "Saw",
  "character:the-ghost-face": "Fun World (Ghost Face)",
  "character:the-demogorgon": "Stranger Things",
  "character:the-executioner": "Silent Hill",
  "character:the-nemesis": "Resident Evil",
  "character:the-cenobite": "Hellraiser",
  "character:the-onryo": "Ringu",
  "character:the-mastermind": "Resident Evil",
  "character:the-xenomorph": "Alien",
  "character:the-good-guy": "Child's Play",
  "character:the-lich": "Dungeons & Dragons",
  "character:the-dark-lord": "Castlevania",
  "character:the-ghoul": "Tokyo Ghoul",
  "character:the-animatronic": "Five Nights at Freddy's",
  "character:the-first": "Stranger Things",
  "character:the-terrifier": "Terrifier",
  // Survivors
  "character:laurie-strode": "Halloween",
  "character:bill-overbeck": "Left 4 Dead",
  "character:quentin-smith": "A Nightmare on Elm Street",
  "character:david-tapp": "Saw",
  "character:ash-williams": "Evil Dead",
  "character:steve-harrington": "Stranger Things",
  "character:nancy-wheeler": "Stranger Things",
  "character:cheryl-mason": "Silent Hill",
  "character:jill-valentine": "Resident Evil",
  "character:leon-scott-kennedy": "Resident Evil",
  "character:yoichi-asakawa": "Ringu",
  "character:ada-wong": "Resident Evil",
  "character:rebecca-chambers": "Resident Evil",
  "character:nicolas-cage": "Nicolas Cage",
  "character:ellen-ripley": "Alien",
  "character:alan-wake": "Alan Wake",
  "character:aestri-yazar-baermar-uraz": "Dungeons & Dragons",
  "character:lara-croft": "Tomb Raider",
  "character:trevor-belmont": "Castlevania",
  "character:rick-grimes": "The Walking Dead",
  "character:michonne-grimes": "The Walking Dead",
  "character:dustin-henderson": "Stranger Things",
  "character:eleven": "Stranger Things",
  "character:sienna-shaw": "Terrifier",
  // Maps
  "map:haddonfield": "Halloween",
  "map:hawkins": "Stranger Things",
  "map:springwood": "A Nightmare on Elm Street",
  "map:the-game": "Saw",
};

/** The series or owner a licensed slot comes from, or null when the slot is not known to be licensed. */
export function knownLicensedHolder(key: string): string | null {
  return Object.hasOwn(KNOWN, key) ? (KNOWN[key] ?? null) : null;
}

/** Every slot key in the list (the tests check that each names a slot the app uses). */
export function knownLicensedKeys(): string[] {
  return Object.keys(KNOWN);
}
