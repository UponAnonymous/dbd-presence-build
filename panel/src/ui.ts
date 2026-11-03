// Shared components. One look for every section: page header, cards, buttons, badges, and the loading, empty and
// error states (UI_SPEC §4–5). Colour is never the only signal: every badge and notice carries its meaning in text.

import { describeError } from "./errors.ts";
import { el, icon, type Child, type IconName } from "./dom.ts";

export type ButtonVariant = "primary" | "secondary" | "danger" | "ghost";

export interface ButtonOptions {
  variant?: ButtonVariant;
  icon?: IconName;
  onClick?: (event: MouseEvent) => void;
  attrs?: Record<string, string | number | boolean | null | undefined>;
  type?: "button" | "submit";
  small?: boolean;
}

export function button(label: string, options: ButtonOptions = {}): HTMLButtonElement {
  const classes = ["btn", `btn-${options.variant ?? "secondary"}`];
  if (options.small) classes.push("btn-small");
  const node = el("button", { className: classes.join(" "), attrs: { type: options.type ?? "button", ...options.attrs } }, options.icon ? icon(options.icon) : null, el("span", { className: "btn-label", text: label }));
  if (options.onClick) node.addEventListener("click", options.onClick);
  return node;
}

/** Shows work in progress on a button: disabled, `aria-busy`, and a label that says what is happening. */
export function setBusy(target: HTMLButtonElement, busy: boolean, busyLabel?: string): void {
  const label = target.querySelector(".btn-label");
  if (busy) {
    if (label && target.dataset["idleLabel"] === undefined) target.dataset["idleLabel"] = label.textContent ?? "";
    if (label && busyLabel) label.textContent = busyLabel;
    target.disabled = true;
    target.setAttribute("aria-busy", "true");
  } else {
    if (label && target.dataset["idleLabel"] !== undefined) label.textContent = target.dataset["idleLabel"];
    delete target.dataset["idleLabel"];
    target.disabled = false;
    target.removeAttribute("aria-busy");
  }
}

export type Tone = "neutral" | "info" | "success" | "warning" | "danger";

export function badge(tone: Tone, text: string): HTMLSpanElement {
  return el("span", { className: `badge badge-${tone}`, text });
}

export function pageHeader(title: string, description: string, ...actions: Child[]): HTMLElement {
  const heading = el("h1", { className: "page-title", text: title, attrs: { tabindex: -1, id: "page-title" } });
  return el(
    "header",
    { className: "page-header" },
    el("div", { className: "page-header-text" }, heading, el("p", { className: "page-description", text: description })),
    actions.some(Boolean) ? el("div", { className: "page-actions" }, ...actions) : null,
  );
}

export function card(title: string | null, ...children: Child[]): HTMLElement {
  return el("section", { className: "card", attrs: title ? { "aria-label": title } : {} }, title ? el("h2", { className: "card-title", text: title }) : null, ...children);
}

export function loading(text: string): HTMLElement {
  return el("div", { className: "state state-loading", attrs: { role: "status" } }, el("span", { className: "spinner", attrs: { "aria-hidden": "true" } }), el("span", { text }));
}

export function emptyState(title: string, text: string, action?: HTMLElement): HTMLElement {
  return el("div", { className: "state state-empty" }, el("p", { className: "state-title", text: title }), el("p", { className: "state-text", text }), action ?? null);
}

/** The error state: what happened, then Reload or Try again. Rendered as an alert so it is announced. */
export function errorState(error: unknown, onRetry: () => void, reload: () => void): HTMLElement {
  const copy = describeError(error);
  const action =
    copy.action === "reload"
      ? button("Reload page", { variant: "primary", icon: "refresh", onClick: reload })
      : copy.action === "retry"
        ? button("Try again", { variant: "secondary", icon: "refresh", onClick: onRetry })
        : null;
  return el(
    "div",
    { className: "state state-error", attrs: { role: "alert" } },
    el("div", { className: "state-error-head" }, icon("alert"), el("p", { className: "state-title", text: copy.title })),
    el("p", { className: "state-text", text: copy.detail }),
    action,
  );
}

const NOTICE_ICONS: Record<Tone, IconName> = { neutral: "info", info: "info", success: "check", warning: "alert", danger: "alert" };

/** An inline message box. `live` makes it a status region (announced politely). */
export function notice(tone: Tone, title: string, text: string | null, options: { live?: boolean; children?: Child[] } = {}): HTMLElement {
  return el(
    "div",
    { className: `notice notice-${tone}`, attrs: options.live ? { role: tone === "danger" ? "alert" : "status" } : {} },
    icon(NOTICE_ICONS[tone], "icon notice-icon"),
    el("div", { className: "notice-body" }, el("p", { className: "notice-title", text: title }), text ? el("p", { className: "notice-text", text }) : null, ...(options.children ?? [])),
  );
}

/** A labelled form field with an optional hint and an error slot (`aria-describedby` links all of them). */
export function field(id: string, label: string, control: HTMLElement, hint?: string): { root: HTMLElement; error: HTMLElement } {
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  control.id = id;
  control.setAttribute("aria-describedby", hint ? `${hintId} ${errorId}` : errorId);
  const error = el("p", { className: "field-error", attrs: { id: errorId, "aria-live": "polite" } });
  const root = el("div", { className: "field" }, el("label", { className: "field-label", text: label, attrs: { for: id } }), control, hint ? el("p", { className: "field-hint", text: hint, attrs: { id: hintId } }) : null, error);
  return { root, error };
}

/** A table whose cells carry their column name for the stacked narrow-screen layout. */
export function table(caption: string, columns: readonly string[], rows: readonly HTMLTableRowElement[], className = ""): HTMLElement {
  const head = el("thead", {}, el("tr", {}, ...columns.map((c) => el("th", { text: c, attrs: { scope: "col" } }))));
  return el("div", { className: "table-wrap" }, el("table", { className: `table ${className}`.trim() }, el("caption", { className: "visually-hidden", text: caption }), head, el("tbody", {}, ...rows)));
}

export function cell(column: string, ...children: Child[]): HTMLTableCellElement {
  return el("td", { attrs: { "data-label": column } }, ...children);
}

/** Primary text with a muted second line. */
export function stacked(primary: Child, secondary: Child): HTMLElement {
  return el("div", { className: "stacked" }, el("span", { className: "stacked-primary" }, primary), secondary ? el("span", { className: "stacked-secondary" }, secondary) : null);
}

export function mono(text: string, className = ""): HTMLElement {
  return el("code", { className: `mono ${className}`.trim(), text });
}
