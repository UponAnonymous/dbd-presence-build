// Audit log (FR-ADM-05): newest first, a page at a time, with each entry's before/after values and hashes, and a
// "Verify chain" button that shows the service's own check of the hash chain.

import type { AuditEntry, ChainReport } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, icon, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { actionLabel, actorParts, count, date, entrySummary, plural, time } from "../format.ts";
import { button, card, cell, emptyState, errorState, loading, mono, notice, pageHeader, setBusy, stacked, table } from "../ui.ts";

export const AUDIT_PAGE_SIZE = 25;

/** JSON as indented text for the details panel; never parsed as markup. */
function jsonText(value: unknown): string {
  return value === null || value === undefined ? "None" : JSON.stringify(value, null, 2);
}

/** What the check says about the last daily export: the newest exported entry is still in the log, unchanged, or no export exists yet. */
function exportLine(report: ChainReport): string {
  const head = report.exportedHead;
  if (head === null) return "No daily export yet, so the log has not been compared with an off-site copy.";
  return `Last daily export matches up to entry #${count(head.seq)}.`;
}

function chainNotice(report: ChainReport): HTMLElement {
  const head = report.exportedHead;
  if (report.ok) {
    const text =
      report.checked === 0
        ? "The log is empty, so there is nothing to check yet."
        : `${plural(report.checked, "entry", "entries")} checked. Every hash matches its entry and every entry links to the one before it.`;
    const extra = report.anchored ? "The oldest remaining entry links to one removed by the retention period, so the check starts there." : null;
    const lines = [el("p", { className: "notice-text", text: exportLine(report) }), ...(extra ? [el("p", { className: "notice-text", text: extra })] : [])];
    return notice("success", "Chain intact", text, { live: true, children: lines });
  }
  if (report.brokenAtSeq === null && head !== null && !head.present) {
    return notice(
      "danger",
      `The log does not match the last daily export (entry #${count(head.seq)})`,
      `${plural(report.checked, "entry", "entries")} link up correctly, but entry #${count(head.seq)}, the newest one in the last daily export, is missing or differs from what was exported. Entries at the end of the log may have been removed or changed outside the service. Keep a copy of the log and investigate before making further changes.`,
      { live: true },
    );
  }
  return notice(
    "danger",
    `Chain broken at entry #${report.brokenAtSeq ?? "?"}`,
    `${plural(report.checked, "entry was", "entries were")} intact before it. That entry, or the link to it, does not match what the service wrote: it was changed or removed outside the service. Keep a copy of the log and investigate before making further changes.`,
    { live: true },
  );
}

function render(container: HTMLElement, ctx: ViewContext): void {
  /** Cursors of the pages before the current one (`null` is the newest page). */
  const history: (number | null)[] = [];
  let cursor: number | null = null;
  let entries: AuditEntry[] = [];
  const expanded = new Set<number>();

  const verifyResult = el("div", { className: "verify-result" });
  const verify = button("Verify chain", { variant: "primary", icon: "check", onClick: () => void runVerify() });
  const listBody = el("div", { className: "card-body" });
  const refresh = button("Refresh", { icon: "refresh", onClick: () => void goTo(null, true) });

  replaceChildren(
    container,
    pageHeader("Audit log", "Every privileged action, who did it and when. Entries are hash-chained, so a change made outside the service shows up when the chain is verified.", refresh),
    card(
      "Chain check",
      el("div", { className: "verify-row" }, el("p", { className: "muted", text: "The service recomputes every entry's hash and checks each link to the entry before it." }), verify),
      verifyResult,
      el("p", { className: "fine-print", text: "Tamper-evident, not tamper-proof: someone with full control of the Cloudflare account could rewrite the whole chain." }),
    ),
    card("Entries", listBody),
  );

  async function runVerify(): Promise<void> {
    setBusy(verify, true, "Verifying…");
    replaceChildren(verifyResult, loading("Checking the chain…"));
    try {
      replaceChildren(verifyResult, chainNotice(await ctx.api.verifyAudit()));
    } catch (error) {
      ctx.reportError(error);
      const copy = describeError(error);
      replaceChildren(verifyResult, notice("danger", `The chain could not be checked: ${copy.title}`, copy.detail, { live: true }));
    } finally {
      setBusy(verify, false);
    }
  }

  async function goTo(before: number | null, reset = false): Promise<void> {
    if (reset) history.length = 0;
    replaceChildren(listBody, loading("Loading entries…"));
    setBusy(refresh, true);
    try {
      entries = await ctx.api.listAudit(before, AUDIT_PAGE_SIZE);
      cursor = before;
      draw();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(listBody, errorState(error, () => void goTo(before), ctx.reload));
    } finally {
      setBusy(refresh, false);
    }
  }

  function draw(): void {
    if (entries.length === 0) {
      replaceChildren(listBody, cursor === null ? emptyState("No entries yet", "Entries appear here as soon as anyone takes a privileged action, such as creating an invite.") : emptyState("No older entries", "This is the start of the log."));
      if (cursor !== null) listBody.appendChild(pager());
      return;
    }
    const rows = entries.flatMap(entryRows);
    replaceChildren(listBody, table("Audit entries, newest first", ["Entry", "Time", "Actor", "Action", "Target", "Details"], rows, "table-audit"), pager());
  }

  function pager(): HTMLElement {
    const first = entries[0];
    const last = entries[entries.length - 1];
    const label = first && last ? `Entries #${count(first.seq)} to #${count(last.seq)}` : "";
    const newer = button("Newer", {
      small: true,
      attrs: { disabled: history.length === 0 },
      onClick: () => {
        const previous = history.pop();
        void goTo(previous ?? null);
      },
    });
    const older = button("Older", {
      small: true,
      attrs: { disabled: !last || entries.length < AUDIT_PAGE_SIZE || last.seq <= 1 },
      onClick: () => {
        if (!last) return;
        history.push(cursor);
        void goTo(last.seq);
      },
    });
    return el("nav", { className: "pager", attrs: { "aria-label": "Audit log pages" } }, newer, el("span", { className: "pager-label", text: label, attrs: { "aria-live": "polite" } }), older);
  }

  function entryRows(entry: AuditEntry): HTMLTableRowElement[] {
    const isOpen = expanded.has(entry.seq);
    const toggle = el(
      "button",
      {
        className: "disclosure disclosure-link",
        attrs: { type: "button", "aria-expanded": isOpen ? "true" : "false", "aria-controls": `entry-${entry.seq}`, "data-entry": entry.seq },
        on: {
          click: () => {
            if (expanded.has(entry.seq)) expanded.delete(entry.seq);
            else expanded.add(entry.seq);
            draw();
            container.querySelector<HTMLButtonElement>(`[data-entry="${entry.seq}"]`)?.focus();
          },
        },
      },
      icon("chevron", "icon disclosure-icon"),
      el("span", { text: isOpen ? "Hide" : "Show" }),
      el("span", { className: "visually-hidden", text: ` details of entry ${entry.seq}` }),
    );
    const actor = actorParts(entry.actor);
    const rows = [
      el(
        "tr",
        {},
        cell("Entry", mono(`#${entry.seq}`)),
        cell("Time", stacked(date(entry.at), time(entry.at))),
        cell("Actor", stacked(actor.kind, actor.detail ? el("span", { className: "break", text: actor.detail }) : null)),
        cell("Action", stacked(actionLabel(entry.action), mono(entry.action, "subtle"))),
        cell("Target", entry.target ? mono(entry.target, "wrap subtle-strong") : el("span", { className: "muted", text: entrySummary(entry) ?? "None" })),
        cell("Details", toggle),
      ),
    ];
    if (isOpen) {
      rows.push(
        el(
          "tr",
          { className: "detail-row" },
          el(
            "td",
            { attrs: { colspan: 6, id: `entry-${entry.seq}` } },
            el(
              "dl",
              { className: "kv" },
              el("dt", { text: "Before" }),
              el("dd", {}, el("pre", { className: "mono pre", text: jsonText(entry.before) })),
              el("dt", { text: "After" }),
              el("dd", {}, el("pre", { className: "mono pre", text: jsonText(entry.after) })),
              el("dt", { text: "Hash" }),
              el("dd", {}, mono(entry.hash, "wrap")),
              el("dt", { text: "Previous hash" }),
              el("dd", {}, mono(entry.prevHash, "wrap")),
            ),
          ),
        ),
      );
    }
    return rows;
  }

  void goTo(null, true);
}

export const auditView: View = { render };
