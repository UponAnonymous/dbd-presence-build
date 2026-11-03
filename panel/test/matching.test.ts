// FR-ADM-08 / PLAN_V2_S5B §2.5, §2.7: "Add pictures from a folder" matches each file name to a slot by the app's import
// rules (code first, then name; a disagreement is never guessed) and tolerates case, spaces, punctuation, accents and
// common variants. Also the panel's own header checks of the chosen files (type, pixel size, size caps).

import { describe, expect, inject, it } from "vitest";
import { SlotCatalog } from "../src/catalog.ts";
import { MAX_FILE, checkImage, imageDimensions } from "../src/image-check.ts";
import { FILE_NAME_HINTS, FileMatcher, fold, stemOf } from "../src/matching.ts";
import { appRoster } from "./app-roster.ts";
import { pngOfSize } from "./fake.ts";

const catalog = new SlotCatalog(appRoster());
const matcher = new FileMatcher(catalog);
const keyOf = (name: string) => {
  const m = matcher.match(name);
  return m.status === "matched" ? m.key : m.status;
};

describe("matching file names to slots", () => {
  it.each([
    ["Ace Visconti.png", "character:ace-visconti"],
    ["ace_visconti.jpg", "character:ace-visconti"],
    ["ACE-VISCONTI.PNG", "character:ace-visconti"],
    ["AceVisconti.png", "character:ace-visconti"],
    ["The Trapper.png", "character:the-trapper"],
    ["Trapper.png", "character:the-trapper"],
    ["the_trapper.jpeg", "character:the-trapper"],
    ["The Ghost Face.png", "character:the-ghost-face"],
    ["Ghost Face.png", "character:the-ghost-face"],
    ["Ghostface.jpg", "character:the-ghost-face"],
    ["Dr. Herman Carter.png", "character:the-doctor"],
    ["The Doctor.png", "character:the-doctor"],
    ["Doctor.png", "character:the-doctor"],
    ["Aestri Yazar & Baermar Uraz.png", "character:aestri-yazar-baermar-uraz"],
    ["Aestri Yazar and Baermar Uraz.png", "character:aestri-yazar-baermar-uraz"],
    ["aestri_yazar_baermar_uraz.png", "character:aestri-yazar-baermar-uraz"],
    ["Jeffrey 'Jeff' Johansen.png", "character:jeff-johansen"],
    ["Jeff Johansen.png", "character:jeff-johansen"],
    ["Élodie Rakoto.png", "character:elodie-rakoto"],
    ["Elodie Rakoto.png", "character:elodie-rakoto"],
    ["The Onryō.png", "character:the-onryo"],
    ["Onryo.png", "character:the-onryo"],
    ["Leon S. Kennedy.png", "character:leon-scott-kennedy"],
    ["Leon Scott Kennedy.png", "character:leon-scott-kennedy"],
    ["Lee Yun-jin.png", "character:lee-yun-jin"],
    ["Yun-Jin Lee.png", "character:lee-yun-jin"],
    ["Detective David Tapp.png", "character:david-tapp"],
    ["Feng Min (1).png", "character:feng-min"],
    ["Dwight Fairfield portrait.png", "character:dwight-fairfield"],
    ["Eleven.png", "character:eleven"],
    ["Michael Myers.png", "character:the-shape"],
    ["Dracula.png", "character:the-dark-lord"],
    ["K27_TheOnryo_Portrait.png", "character:the-onryo"],
    ["T_UI_K27_TheOnryo_Portrait.png", "character:the-onryo"],
    ["K27.png", "character:the-onryo"],
    ["k27 onryo.png", "character:the-onryo"],
    ["Killer icon.png", "role:killer"],
    ["killer.png", "role:killer"],
    ["Survivor.png", "role:survivor"],
    ["Generic killer portrait.png", "character:generic-killer"],
    ["App mark.png", "app:mark"],
    ["status-hooked.png", "app:status-hooked"],
    ["Result Escaped.png", "app:result-escaped"],
    ["Coldwind Farm.jpg", "map:coldwind-farm"],
    ["hawkins.png", "map:hawkins"],
    ["dwight_fairfield_v2_final_alt.png", "character:dwight-fairfield"],
    ["holiday photo.png", "unmatched"],
    [".DS_Store", "unmatched"],
  ])("%s → %s", (name, expected) => {
    expect(keyOf(name)).toBe(expected);
  });

  it("matches every owner-pack name of the shared fixture by its code, with and without the app's affixes", () => {
    const fixture = JSON.parse(inject("importNames")) as { files: { stem: string; id: string }[] };
    for (const { stem, id } of fixture.files) {
      for (const name of [stem, `${stem}.png`, `${stem}_Portrait.png`, `T_UI_${stem}_Portrait.png`]) {
        expect(matcher.match(name), name).toEqual({ status: "matched", key: `character:${id}`, basis: "code" });
      }
    }
  });

  it("says how a file matched", () => {
    expect(matcher.match("K01_TheTrapper.png")).toMatchObject({ basis: "code" });
    expect(matcher.match("The Trapper.png")).toMatchObject({ basis: "name" });
    expect(matcher.match("Dr. Herman Carter.png")).toMatchObject({ basis: "similar" });
    expect(matcher.match("dwight_fairfield_alt.png")).toMatchObject({ basis: "partial" });
  });

  it("never guesses when the code and the name disagree", () => {
    expect(matcher.match("K01_TheWraith.png")).toEqual({ status: "conflict", key: "character:the-trapper", other: "character:the-wraith" });
  });

  it("chooses nothing when a name fits two slots", () => {
    const m = matcher.match("david.png");
    expect(m.status).toBe("unmatched");
    const two = matcher.match("david-king-david-tapp.png");
    expect(two).toEqual({ status: "ambiguous", keys: ["character:david-king", "character:david-tapp"] });
  });

  it("an unknown code is not a match", () => {
    expect(matcher.match("K99.png").status).toBe("unmatched");
  });

  it("strips the app's affixes and folds like the app", () => {
    expect(stemOf("Portraits/T_UI_S24_ElodieRakoto_Portrait.png")).toBe("S24_ElodieRakoto");
    expect(fold("The Onryō")).toBe(fold("TheOnryo"));
    expect(fold("Élodie")).toBe("elodie");
  });

  it("every file-name hint belongs to a character the app knows", () => {
    for (const id of Object.keys(FILE_NAME_HINTS)) expect(catalog.isKnown(`character:${id}`), id).toBe(true);
  });

  it("without the roster still matches roles, tiles and maps", () => {
    const fixedOnly = new FileMatcher(new SlotCatalog(null));
    expect(fixedOnly.match("Killer icon.png")).toMatchObject({ status: "matched", key: "role:killer" });
    expect(fixedOnly.match("The Trapper.png").status).toBe("unmatched");
  });
});

describe("checking chosen files before upload", () => {
  it("reads the pixel size from a PNG header", () => {
    expect(imageDimensions(pngOfSize(512, 256), "image/png")).toEqual({ width: 512, height: 256 });
    expect(checkImage(pngOfSize(512, 512))).toEqual({ ok: true, image: { type: "image/png", width: 512, height: 512 } });
  });

  it("reads the pixel size from a JPEG frame header, past other segments", () => {
    const jpeg = new Uint8Array([0xff, 0xd8, 0xff, 0xdb, 0x00, 0x04, 0x00, 0x00, 0xff, 0xc0, 0x00, 0x11, 0x08, 0x02, 0x00, 0x03, 0x00, 0x03]);
    expect(imageDimensions(jpeg, "image/jpeg")).toEqual({ width: 768, height: 512 });
  });

  it("refuses what single upload refuses: empty, over 2 MB, not PNG or JPEG, over 1024 pixels", () => {
    expect(checkImage(new Uint8Array(0))).toEqual({ ok: false, reason: "empty" });
    expect(checkImage(new Uint8Array(MAX_FILE + 1))).toEqual({ ok: false, reason: "too_large" });
    expect(checkImage(new TextEncoder().encode("not an image"))).toEqual({ ok: false, reason: "unknown_format" });
    expect(checkImage(pngOfSize(2048, 1024))).toEqual({ ok: false, reason: "dimensions_out_of_range" });
  });

  it("leaves a header it cannot read to the service", () => {
    expect(checkImage(new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13]))).toEqual({ ok: true, image: { type: "image/png", width: null, height: null } });
  });
});
