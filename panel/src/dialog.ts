// Confirmation for destructive and important actions (FR-ADM-05: "destructive actions need confirmation").
// A native modal <dialog>: it traps focus, Escape cancels, and focus returns to the control that opened it.
// Focus starts on Cancel, so pressing Enter by reflex never confirms.

import { el } from "./dom.ts";
import { button } from "./ui.ts";

export interface ConfirmOptions {
  title: string;
  /** Paragraphs of plain text. */
  body: readonly string[];
  confirmLabel: string;
  cancelLabel?: string;
  danger?: boolean;
  /** When set, the confirm button stays disabled until this exact text is typed. */
  typed?: { expected: string; label: string };
}

let sequence = 0;

export function confirmDialog(doc: Document, options: ConfirmOptions): Promise<boolean> {
  const opener = doc.activeElement instanceof HTMLElement ? doc.activeElement : null;
  const n = ++sequence;
  const titleId = `dialog-title-${n}`;
  const bodyId = `dialog-body-${n}`;

  return new Promise((resolve) => {
    let settled = false;
    const dialog = el("dialog", { className: "dialog", attrs: { "aria-labelledby": titleId, "aria-describedby": bodyId } });
    const confirm = button(options.confirmLabel, { variant: options.danger ? "danger" : "primary" });
    const cancel = button(options.cancelLabel ?? "Cancel", { variant: "secondary" });

    let input: HTMLInputElement | null = null;
    let typedBlock: HTMLElement | null = null;
    if (options.typed) {
      const expected = options.typed.expected;
      input = el("input", { className: "input mono", attrs: { type: "text", id: `dialog-typed-${n}`, autocomplete: "off", spellcheck: "false", autocapitalize: "off" } });
      typedBlock = el("div", { className: "field" }, el("label", { className: "field-label", attrs: { for: `dialog-typed-${n}` } }, options.typed.label, " ", el("code", { className: "mono", text: expected })), input);
      confirm.disabled = true;
      input.addEventListener("input", () => {
        confirm.disabled = input!.value.trim() !== expected;
      });
    }

    const finish = (result: boolean): void => {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      dialog.remove();
      opener?.focus();
      resolve(result);
    };

    confirm.addEventListener("click", () => {
      if (input && input.value.trim() !== options.typed!.expected) return;
      finish(true);
    });
    cancel.addEventListener("click", () => finish(false));
    dialog.addEventListener("cancel", (event) => {
      event.preventDefault();
      finish(false);
    });
    dialog.addEventListener("close", () => finish(false));

    const parts = [
      el("h2", { className: "dialog-title", text: options.title, attrs: { id: titleId } }),
      el("div", { className: "dialog-body", attrs: { id: bodyId } }, ...options.body.map((p) => el("p", { text: p }))),
      typedBlock,
      el("div", { className: "dialog-actions" }, cancel, confirm),
    ];
    for (const part of parts) if (part) dialog.appendChild(part);
    (doc.body ?? doc.documentElement).appendChild(dialog);
    dialog.showModal();
    (input ?? cancel).focus();
  });
}
