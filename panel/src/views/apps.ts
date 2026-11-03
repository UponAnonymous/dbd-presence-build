// Discord applications (D2, FR-ADM-08, FR-ONL-08): the applications the signed config lists, and which one apps use by
// default. The whole list is saved at once; the rules match the app's own parser, so a saved list is never refused.

import type { AppList, DiscordApp } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { plural } from "../format.ts";
import { badge, button, card, errorState, loading, notice, pageHeader, setBusy } from "../ui.ts";

const MAX_APPS = 10;
const KEY = /^[a-z0-9-]{1,32}$/;
const APP_ID = /^\d{15,22}$/;
const CONTROL = /[\p{Cc}]/u;

interface Row {
  app: DiscordApp;
  /** True for a row added in this session and not saved yet. */
  added: boolean;
}

/** The first problem with the list, as text naming the row, or null. */
export function appListProblem(apps: readonly DiscordApp[]): { row: number; field: keyof DiscordApp; text: string } | null {
  if (apps.length === 0) return { row: 0, field: "key", text: "Keep at least one application." };
  for (let i = 0; i < apps.length; i++) {
    const a = apps[i]!;
    if (!KEY.test(a.key)) return { row: i, field: "key", text: "Use 1 to 32 lower-case letters, digits and hyphens." };
    if (!APP_ID.test(a.applicationId)) return { row: i, field: "applicationId", text: "Paste the Application ID from the Discord Developer Portal: 15 to 22 digits." };
    if (a.name.trim() === "" || a.name.length > 128 || CONTROL.test(a.name)) return { row: i, field: "name", text: "Enter a name of up to 128 characters." };
    if (a.description.length > 256 || CONTROL.test(a.description)) return { row: i, field: "description", text: "Shorten the description to 256 characters." };
    if (apps.findIndex((b) => b.key === a.key) !== i) return { row: i, field: "key", text: "Another application already uses this key." };
    if (apps.findIndex((b) => b.applicationId === a.applicationId) !== i) return { row: i, field: "applicationId", text: "Another row already has this Application ID." };
  }
  return null;
}

function render(container: HTMLElement, ctx: ViewContext): void {
  let saved: AppList | null = null;
  let rows: Row[] = [];
  let defaultKey = "";
  const body = el("div", { className: "card-body" });
  const message = el("div", { className: "inline-message" });
  const version = el("p", { className: "card-summary muted" });

  replaceChildren(
    container,
    pageHeader("Discord apps", "The Discord applications the app can show your status under. Apps receive this list in the signed configuration and use the default one."),
    message,
    card("Applications", version, body),
  );

  async function load(): Promise<void> {
    replaceChildren(message);
    replaceChildren(body, loading("Loading the application list…"));
    try {
      saved = await ctx.api.getApps();
      reset();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(body, errorState(error, () => void load(), ctx.reload));
    }
  }

  function reset(): void {
    if (!saved) return;
    rows = saved.applications.map((app) => ({ app: { ...app }, added: false }));
    defaultKey = saved.defaultApplication;
    version.textContent = `Signed configuration version ${saved.configVersion}. Saving a change raises it; apps pick it up within a few minutes of their next check.`;
    draw();
  }

  function changed(): boolean {
    if (!saved) return false;
    return defaultKey !== saved.defaultApplication || JSON.stringify(rows.map((r) => r.app)) !== JSON.stringify(saved.applications);
  }

  function draw(focus?: { row: number; field: keyof DiscordApp }): void {
    const save = button("Save applications", { variant: "primary", attrs: { disabled: !changed() } });
    const discard = button("Discard changes", { attrs: { disabled: !changed() }, onClick: reset });
    const add = button("Add application", {
      icon: "plus",
      attrs: { disabled: rows.length >= MAX_APPS },
      onClick: () => {
        rows.push({ app: { key: "", applicationId: "", name: "", description: "" }, added: true });
        draw({ row: rows.length - 1, field: "key" });
      },
    });
    const error = el("p", { className: "field-error", attrs: { id: "apps-error", "aria-live": "polite" } });
    const refreshButtons = () => {
      save.disabled = !changed();
      discard.disabled = !changed();
    };

    const items = rows.map((row, i) => {
      const n = i + 1;
      const input = (name: keyof DiscordApp, label: string, attrs: Record<string, string | number | boolean>) => {
        const id = `app-${n}-${name}`;
        const control = el("input", { className: `input${name === "applicationId" || name === "key" ? " mono" : ""}`, attrs: { type: "text", id, value: row.app[name], autocomplete: "off", spellcheck: "false", ...attrs } });
        control.addEventListener("input", () => {
          const before = row.app.key;
          row.app[name] = control.value;
          if (name === "key" && defaultKey === before) defaultKey = control.value;
          refreshButtons();
        });
        return el("div", { className: "field" }, el("label", { className: "field-label", text: label, attrs: { for: id } }), control);
      };
      const isDefault = defaultKey === row.app.key && row.app.key !== "";
      const radio = el("input", { className: "radio", attrs: { type: "radio", name: "default-app", id: `app-${n}-default`, checked: isDefault } });
      radio.addEventListener("change", () => {
        defaultKey = row.app.key;
        draw();
      });
      const remove = button("Remove", {
        small: true,
        variant: "danger",
        attrs: { "aria-label": `Remove ${row.app.name || `application ${n}`}`, disabled: rows.length === 1 || isDefault },
        onClick: () => {
          rows.splice(i, 1);
          draw();
        },
      });
      return el(
        "li",
        { className: "app-row" },
        el("div", { className: "app-row-head" }, el("span", { className: "app-row-title", text: row.app.name || `Application ${n}` }), isDefault ? badge("info", "Default") : null, row.added ? badge("neutral", "Not saved") : null),
        el(
          "div",
          { className: "form-two" },
          input("name", "Name", { maxlength: 128 }),
          input("applicationId", "Application ID", { maxlength: 22, inputmode: "numeric" }),
          input("key", "Key", { maxlength: 32 }),
          el("div", { className: "field-span" }, input("description", "Description (optional)", { maxlength: 256 })),
        ),
        el("div", { className: "app-row-foot" }, el("label", { className: "inline-check", attrs: { for: `app-${n}-default` } }, radio, el("span", { text: "Use by default" })), remove),
      );
    });

    save.addEventListener("click", () => void apply());

    async function apply(): Promise<void> {
      error.textContent = "";
      const apps = rows.map((r) => ({ key: r.app.key.trim(), applicationId: r.app.applicationId.trim(), name: r.app.name.trim(), description: r.app.description.trim() }));
      const problem = appListProblem(apps);
      if (problem) {
        error.textContent = `Application ${problem.row + 1}: ${problem.text}`;
        container.querySelector<HTMLElement>(`#app-${problem.row + 1}-${problem.field}`)?.focus();
        return;
      }
      if (!apps.some((a) => a.key === defaultKey)) {
        error.textContent = "Choose which application apps use by default.";
        return;
      }
      const before = saved!;
      const addedApps = apps.filter((a) => !before.applications.some((b) => b.key === a.key));
      const removedApps = before.applications.filter((b) => !apps.some((a) => a.key === b.key));
      const lines = [`${plural(apps.length, "application", "applications")} will be listed in the signed configuration. Apps pick up the change at their next check.`];
      if (addedApps.length > 0) lines.push(`Added: ${addedApps.map((a) => a.name).join(", ")}.`);
      if (removedApps.length > 0) lines.push(`Removed: ${removedApps.map((a) => a.name).join(", ")}. Apps using it switch to the default.`);
      if (defaultKey !== before.defaultApplication) lines.push(`New default: ${apps.find((a) => a.key === defaultKey)!.name}.`);
      const ok = await ctx.confirm({ title: "Save the application list?", body: lines, confirmLabel: "Save applications" });
      if (!ok) return;
      setBusy(save, true, "Saving…");
      try {
        saved = await ctx.api.saveApps(apps, defaultKey);
        reset();
        replaceChildren(message, notice("success", "Applications saved", `Signed configuration version ${saved.configVersion}.`, { live: true }));
        ctx.announce("Application list saved.");
      } catch (failure) {
        setBusy(save, false);
        ctx.reportError(failure);
        const copy = describeError(failure);
        replaceChildren(message, notice("danger", `The list was not saved: ${copy.title}`, copy.detail, { live: true }));
      }
    }

    replaceChildren(
      body,
      el("ul", { className: "app-list", attrs: { "aria-label": "Discord applications" } }, ...items),
      el("p", { className: "fine-print", text: "The key is how the app refers to an application; keep it stable once apps use it. The Application ID is public: it is on the application's General Information page in the Discord Developer Portal." }),
      error,
      el("div", { className: "form-actions" }, add, discard, save),
    );
    if (focus) container.querySelector<HTMLElement>(`#app-${focus.row + 1}-${focus.field}`)?.focus();
  }

  void load();
}

export const appsView: View = { render };
