// Characters (FR-ADM-12, FR-ADM-13; PLAN_V2_S5B §2.8, owner decision D35): the signed character list. New characters are
// learned from the owner's own game: after the same name was seen in 3 separate matches the service adds it for everyone,
// and it shows here as "Added automatically: <name> (<role>) · Undo". Undo removes it and blocks the name until the owner
// allows it again. The owner can also add, rename and remove characters by hand, choose which app accounts are their own
// game accounts (only those report names), and see which characters the default pack has no picture for. Names are
// rendered as text only; thumbnails come only from this environment's content host (src/dom.ts packImage).

import type { Pack, RosterCharacter, RosterState, User } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, packImage, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { date, plural, relative } from "../format.ts";
import { badge, button, card, cell, emptyState, errorState, field, loading, mono, notice, pageHeader, setBusy, stacked, table } from "../ui.ts";

const ROLE_LABEL = { killer: "Killer", survivor: "Survivor" } as const;
const SOURCE_LABEL = { seed: "Ships with the app", owner: "Added by you", auto: "Added automatically" } as const;
const COLUMNS = ["Code", "Name", "Role", "Source", "Default picture", "Status", "Actions"] as const;

function nameOf(character: RosterCharacter): string {
  return character.displayName === character.screenName ? character.displayName : `${character.displayName} (on screen: ${character.screenName})`;
}

function render(container: HTMLElement, ctx: ViewContext): void {
  let state: RosterState | null = null;
  let users: User[] = [];
  let defaultPack: Pack | null = null;
  let renaming: string | null = null;
  const body = el("div", { className: "stack" });
  const message = el("div", { className: "inline-message" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void load() });

  replaceChildren(
    container,
    pageHeader(
      "Characters",
      "The character list every app gets, signed by the service. New characters are learned from your own game: once a name not in the list was seen in 3 separate matches, it is added for everyone.",
      refresh,
    ),
    message,
    body,
  );

  async function load(): Promise<void> {
    replaceChildren(message);
    replaceChildren(body, loading("Loading the character list…"));
    setBusy(refresh, true);
    try {
      const [roster, userList, packs] = await Promise.all([ctx.api.getRoster(), ctx.api.listUsers(), ctx.api.listPacks()]);
      state = roster;
      users = userList;
      defaultPack = packs.packs.find((p) => p.default) ?? null;
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function say(node: HTMLElement): void {
    replaceChildren(message, node);
  }

  function failure(prefix: string, error: unknown): void {
    ctx.reportError(error);
    const copy = describeError(error);
    say(notice("danger", `${prefix}: ${copy.title}`, copy.detail, { live: true }));
  }

  /** Runs a change and redraws from the service's answer. */
  async function change(trigger: HTMLButtonElement, busy: string, run: () => Promise<RosterState>, done: string, failed: string): Promise<boolean> {
    setBusy(trigger, true, busy);
    try {
      state = await run();
      renaming = null;
      draw();
      say(notice("success", done, null, { live: true }));
      ctx.announce(done);
      return true;
    } catch (error) {
      setBusy(trigger, false);
      failure(failed, error);
      return false;
    }
  }

  function draw(): void {
    if (!state) return;
    const s = state;
    replaceChildren(
      body,
      s.roster.stale
        ? notice(
            "warning",
            "The character list has not been re-signed for 25 days",
            `The service re-signs it every week. Apps stop trusting a list after 30 days (on ${date(s.roster.expiresAt)}) and keep their last good one. Check that the service's daily job runs.`,
            { live: true },
          )
        : null,
      summaryCard(s),
      learnedCard(s),
      pendingCard(s),
      reportersCard(s),
      blockedCard(s),
      addCard(s),
      listCard(s),
    );
  }

  function summaryCard(s: RosterState): HTMLElement {
    const active = s.roster.characters.filter((c) => !c.withdrawn);
    return card(
      "Signed character list",
      el(
        "p",
        { className: "muted" },
        `${plural(active.length, "character", "characters")} (${plural(active.filter((c) => c.role === "killer").length, "killer", "killers")}, ${plural(active.filter((c) => c.role === "survivor").length, "survivor", "survivors")}) · version `,
        mono(String(s.roster.version)),
        ` · signed ${relative(s.roster.signedAt, ctx.now())}, valid until ${date(s.roster.expiresAt)}`,
      ),
    );
  }

  function learnedCard(s: RosterState): HTMLElement {
    const learned = s.roster.characters.filter((c) => c.source === "auto" && !c.withdrawn);
    if (learned.length === 0) {
      return card("Added automatically", el("p", { className: "muted", text: "Nothing yet. Characters learned from your game appear here, each with an Undo." }));
    }
    return card(
      "Added automatically",
      el(
        "ul",
        { className: "device-list", attrs: { "aria-label": "Characters added automatically" } },
        ...learned.map((c) =>
          el(
            "li",
            { className: "device-item" },
            el("p", { text: `Added automatically: ${c.displayName} (${ROLE_LABEL[c.role].toLowerCase()})` }),
            el("div", { className: "device-meta" }, mono(c.code), c.addedAt ? el("span", { text: `Added ${relative(c.addedAt, ctx.now())}` }) : null),
            button("Undo", { small: true, variant: "danger", attrs: { "aria-label": `Undo ${c.displayName}` }, onClick: (e) => void withdraw(c, true, e.currentTarget as HTMLButtonElement) }),
          ),
        ),
      ),
    );
  }

  function pendingCard(s: RosterState): HTMLElement {
    if (s.pending.length === 0) return card("Seen in your game, not added yet", el("p", { className: "muted", text: "No unknown names are waiting." }));
    const rows = s.pending.map((p) =>
      el(
        "tr",
        {},
        cell("Name", p.name),
        cell("Role", ROLE_LABEL[p.role]),
        cell("Matches", `${p.matches} of ${s.limits.matchesToAdd}`),
        cell("Last seen", stacked(relative(p.lastSeenAt, ctx.now()), date(p.lastSeenAt))),
      ),
    );
    return card(
      "Seen in your game, not added yet",
      el("p", {
        className: "muted",
        text: `A name is added once it was seen in ${s.limits.matchesToAdd} separate matches, spelled the same way, in reports at least an hour apart and at least two days from the first to the latest. At most three characters are added a day. Names seen less often are forgotten after 30 days.`,
      }),
      table("Names waiting to be added", ["Name", "Role", "Matches", "Last seen"], rows),
      button("Clear pending names", {
        small: true,
        attrs: { "aria-label": "Clear all names waiting to be added" },
        onClick: (e) => void clearPending(e.currentTarget as HTMLButtonElement),
      }),
    );
  }

  function reportersCard(s: RosterState): HTMLElement {
    const candidates = users.filter((u) => u.status === "active" && !u.rosterReporter);
    const select = el(
      "select",
      { className: "input select" },
      el("option", { text: "Choose an account", attrs: { value: "" } }),
      ...candidates.map((u) => el("option", { text: u.displayName, attrs: { value: u.id } })),
    );
    const chooser = field("roster-reporter", "Your game account", select, "Only the accounts you mark here report names; your friends' apps never do.");
    const add = button("Mark as my game account", {
      onClick: (e) => {
        const chosen = users.find((u) => u.id === select.value);
        if (!chosen) {
          chooser.error.textContent = "Choose the account you play with.";
          select.focus();
          return;
        }
        void setReporter(chosen.id, chosen.displayName, true, e.currentTarget as HTMLButtonElement);
      },
    });
    return card(
      "Learning from your game",
      s.reporters.length === 0
        ? notice("info", "No game account is marked yet", "Mark the app account you play with, so your app can report new character names.")
        : el(
            "ul",
            { className: "device-list", attrs: { "aria-label": "Your game accounts" } },
            ...s.reporters.map((r) =>
              el(
                "li",
                { className: "device-item" },
                el("p", { text: r.displayName }),
                button("Stop reporting", { small: true, attrs: { "aria-label": `Stop reporting from ${r.displayName}` }, onClick: (e) => void setReporter(r.id, r.displayName, false, e.currentTarget as HTMLButtonElement) }),
              ),
            ),
          ),
      s.reporters.length < s.limits.maxReporters && candidates.length > 0 ? el("div", { className: "form-grid" }, chooser.root, el("div", { className: "form-actions" }, add)) : null,
    );
  }

  function blockedCard(s: RosterState): HTMLElement {
    if (s.blocked.length === 0) return card("Removed names", el("p", { className: "muted", text: "No names are blocked." }));
    return card(
      "Removed names",
      el("p", { className: "muted", text: "Names you removed are never added again on their own until you allow them." }),
      el(
        "ul",
        { className: "device-list", attrs: { "aria-label": "Removed names" } },
        ...s.blocked.map((b) =>
          el(
            "li",
            { className: "device-item" },
            el("p", { text: b.name }),
            el("span", { className: "muted", text: `Removed ${relative(b.blockedAt, ctx.now())}` }),
            button("Allow again", {
              small: true,
              attrs: { "aria-label": `Allow ${b.name} again` },
              onClick: (e) => void change(e.currentTarget as HTMLButtonElement, "Saving…", () => ctx.api.allowRosterName(b.name), `${b.name} can be learned again.`, `${b.name} was not allowed again`),
            }),
          ),
        ),
      ),
    );
  }

  function addCard(s: RosterState): HTMLElement {
    const role = el("select", { className: "input select" }, el("option", { text: "Killer", attrs: { value: "killer" } }), el("option", { text: "Survivor", attrs: { value: "survivor" } }));
    const codeInput = el("input", { className: "input mono", attrs: { type: "text", maxlength: 3, autocomplete: "off", value: s.roster.nextCodes.killer ?? "" } });
    role.addEventListener("change", () => {
      codeInput.value = (role.value === "killer" ? s.roster.nextCodes.killer : s.roster.nextCodes.survivor) ?? "";
    });
    const display = el("input", { className: "input", attrs: { type: "text", maxlength: 80, autocomplete: "off" } });
    const screen = el("input", { className: "input", attrs: { type: "text", maxlength: 80, autocomplete: "off" } });
    const roleField = field("add-character-role", "Role", role);
    const codeField = field("add-character-code", "Code", codeInput, "The prefix of the game's portrait file names, for example K45. Pre-filled with the next free code; it can never change.");
    const displayField = field("add-character-name", "Name", display, "As the Characters page shows it.");
    const screenField = field("add-character-screen", "Name on screen (optional)", screen, "As the game prints it, if different. Detection matches this name.");
    const submit = button("Add character", { variant: "primary", type: "submit", icon: "plus" });
    const form = el(
      "form",
      { className: "form-grid", attrs: { novalidate: true, "aria-label": "Add a character" } },
      roleField.root,
      codeField.root,
      displayField.root,
      screenField.root,
      el("div", { className: "form-actions" }, submit),
    );
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      codeField.error.textContent = "";
      displayField.error.textContent = "";
      const codeValue = codeInput.value.trim().toUpperCase();
      const wanted = role.value === "killer" ? "K" : "S";
      if (!/^[KS][0-9]{2}$/.test(codeValue) || codeValue[0] !== wanted) {
        codeField.error.textContent = `Enter ${wanted} and two digits.`;
        codeInput.focus();
        return;
      }
      const name = display.value.trim();
      if (name.length === 0) {
        displayField.error.textContent = "Enter the character's name.";
        display.focus();
        return;
      }
      const onScreen = screen.value.trim();
      void change(
        submit,
        "Adding…",
        () => ctx.api.addRosterCharacter({ code: codeValue, displayName: name, screenName: onScreen.length > 0 ? onScreen : null, id: null }),
        `${name} added. Apps get it at their next check.`,
        `${name} was not added`,
      );
    });
    return card("Add a character by hand", form);
  }

  function listCard(s: RosterState): HTMLElement {
    const rows: HTMLTableRowElement[] = [];
    for (const c of s.roster.characters) rows.push(characterRow(c));
    if (rows.length === 0) return card("All characters", emptyState("No characters", "The list is empty."));
    const missing = s.roster.characters.filter((c) => !c.withdrawn && pictureOf(c) === null).length;
    return card(
      "All characters",
      defaultPack
        ? el("p", { className: "muted", text: `Default pictures come from ${defaultPack.name}. ${missing === 0 ? "Every character has one." : `${plural(missing, "character has", "characters have")} no picture there.`}` })
        : notice("info", "No default pack is chosen", "Choose one in Packs: its character pictures are what apps show when a player has not chosen their own."),
      table("All characters, by code", COLUMNS, rows, "table-users"),
    );
  }

  function pictureOf(c: RosterCharacter): HTMLElement | null {
    const entry = defaultPack?.entries.find((e) => e.key === `character:${c.id}` && e.live && !e.withdrawn);
    return entry ? el("div", { className: "thumb" }, packImage(entry.url, ctx.environment, `${c.displayName}, default picture`, 40)) : null;
  }

  function characterRow(c: RosterCharacter): HTMLTableRowElement {
    const editable = c.source !== "seed";
    const actions = el("div", { className: "row-actions" });
    let nameCell: HTMLElement = stacked(nameOf(c), c.aliases.length > 0 ? `Also: ${c.aliases.map((a) => a.name).join(", ")}` : null);
    if (editable && renaming === c.code) {
      const display = el("input", { className: "input", attrs: { type: "text", maxlength: 80, autocomplete: "off", value: c.displayName, "aria-label": `New name for ${c.code}` } });
      const screen = el("input", { className: "input", attrs: { type: "text", maxlength: 80, autocomplete: "off", value: c.screenName, "aria-label": `New on-screen name for ${c.code}` } });
      nameCell = el("div", { className: "stack" }, display, screen);
      actions.append(
        button("Save", {
          small: true,
          variant: "primary",
          attrs: { "aria-label": `Save the name of ${c.code}` },
          onClick: (e) =>
            void change(
              e.currentTarget as HTMLButtonElement,
              "Saving…",
              () => ctx.api.renameRosterCharacter(c.code, { displayName: display.value.trim(), screenName: screen.value.trim() }),
              `${c.code} renamed.`,
              `${c.code} was not renamed`,
            ),
        }),
        button("Cancel", { small: true, onClick: () => ((renaming = null), draw()) }),
      );
    } else if (editable) {
      actions.append(
        button("Rename", { small: true, attrs: { "aria-label": `Rename ${c.displayName}` }, onClick: () => ((renaming = c.code), draw()) }),
        button(c.withdrawn ? "Restore" : c.source === "auto" ? "Undo" : "Remove", {
          small: true,
          variant: c.withdrawn ? "secondary" : "danger",
          attrs: { "aria-label": `${c.withdrawn ? "Restore" : c.source === "auto" ? "Undo" : "Remove"} ${c.displayName}` },
          onClick: (e) => void withdraw(c, !c.withdrawn, e.currentTarget as HTMLButtonElement),
        }),
      );
    } else {
      actions.append(el("span", { className: "muted", text: "Changes with an app release" }));
    }
    return el(
      "tr",
      { attrs: { "data-code": c.code } },
      cell("Code", mono(c.code)),
      cell("Name", nameCell),
      cell("Role", ROLE_LABEL[c.role]),
      cell("Source", SOURCE_LABEL[c.source]),
      cell("Default picture", pictureOf(c) ?? el("span", { className: "muted", text: "No picture" })),
      cell("Status", c.withdrawn ? stacked(badge("danger", "Removed"), c.withdrawnAt ? date(c.withdrawnAt) : null) : badge("success", "In the list")),
      cell("Actions", actions),
    );
  }

  async function clearPending(trigger: HTMLButtonElement): Promise<void> {
    const ok = await ctx.confirm({
      title: "Clear the names waiting to be added?",
      body: ["Every name seen in your game but not added yet is forgotten. Characters already added and removed names stay as they are."],
      confirmLabel: "Clear",
      danger: true,
    });
    if (!ok) return;
    await change(trigger, "Clearing…", () => ctx.api.clearRosterSightings(), "Waiting names cleared.", "The waiting names were not cleared");
  }

  async function withdraw(c: RosterCharacter, withdrawn: boolean, trigger: HTMLButtonElement): Promise<void> {
    if (withdrawn) {
      const ok = await ctx.confirm({
        title: c.source === "auto" ? `Undo ${c.displayName}?` : `Remove ${c.displayName}?`,
        body: [
          "Apps stop showing and detecting this character at their next check. Its code and ID stay reserved, so you can restore it later.",
          "The name is blocked: it will not be added again on its own until you allow it under Removed names.",
        ],
        confirmLabel: c.source === "auto" ? "Undo" : "Remove",
        danger: true,
      });
      if (!ok) return;
    }
    await change(
      trigger,
      withdrawn ? "Removing…" : "Restoring…",
      () => ctx.api.withdrawRosterCharacter(c.code, withdrawn),
      withdrawn ? `${c.displayName} removed.` : `${c.displayName} restored.`,
      withdrawn ? `${c.displayName} was not removed` : `${c.displayName} was not restored`,
    );
  }

  async function setReporter(userId: string, displayName: string, enabled: boolean, trigger: HTMLButtonElement): Promise<void> {
    if (enabled) {
      const ok = await ctx.confirm({
        title: `Mark ${displayName} as your game account?`,
        body: [
          "This account's app then reports character names it reads on screen that are not in the list: the name, the role and a random match number. No player names and no images are sent.",
          "Only mark the account you play with yourself.",
        ],
        confirmLabel: "Mark as my game account",
      });
      if (!ok) return;
    }
    setBusy(trigger, true, "Saving…");
    try {
      await ctx.api.setRosterReporter(userId, enabled);
      users = users.map((u) => (u.id === userId ? { ...u, rosterReporter: enabled } : u));
      state = await ctx.api.getRoster();
      draw();
      const done = enabled ? `${displayName} reports new characters.` : `${displayName} no longer reports.`;
      say(notice("success", done, null, { live: true }));
      ctx.announce(done);
    } catch (error) {
      setBusy(trigger, false);
      failure(`${displayName} was not changed`, error);
    }
  }

  void load();
}

export const rosterView: View = { render };
