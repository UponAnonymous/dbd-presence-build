// The frame around every section: environment banner and badge, navigation, theme choice, global banners (expired
// session, offline) and the status line. Sections are addressed by the URL fragment (#/invites …) so the site stays
// static. Only sections that exist are listed (FR-ADM-01: Invites, Users and Audit log in V2-S3; Packs, Discord
// applications and Access & features in V2-S4; Uploads in V2-S5; Characters in V2-S5b; Releases in V2-S6; Dashboard and
// Settings in V2-S7). Dashboard is the first section and the one an unknown address falls back to.

import type { AdminApi } from "./api.ts";
import type { View, ViewContext } from "./context.ts";
import { confirmDialog } from "./dialog.ts";
import { el, icon, replaceChildren, routeLink, type IconName } from "./dom.ts";
import { environmentFor } from "./env.ts";
import { isSessionError } from "./errors.ts";
import { applyTheme, readThemeChoice, saveThemeChoice, type ThemeChoice } from "./theme.ts";
import { button } from "./ui.ts";
import { accessView } from "./views/access.ts";
import { appsView } from "./views/apps.ts";
import { auditView } from "./views/audit.ts";
import { dashboardView } from "./views/dashboard.ts";
import { invitesView } from "./views/invites.ts";
import { packsView } from "./views/packs.ts";
import { releasesView } from "./views/releases.ts";
import { rosterView } from "./views/roster.ts";
import { settingsView } from "./views/settings.ts";
import { uploadsView } from "./views/uploads.ts";
import { usersView } from "./views/users.ts";

interface Section {
  id: string;
  label: string;
  icon: IconName;
  view: View;
}

export const SECTIONS: readonly Section[] = [
  { id: "dashboard", label: "Dashboard", icon: "dashboard", view: dashboardView },
  { id: "invites", label: "Invites", icon: "invites", view: invitesView },
  { id: "users", label: "Users", icon: "users", view: usersView },
  { id: "uploads", label: "Uploads", icon: "uploads", view: uploadsView },
  { id: "packs", label: "Packs", icon: "packs", view: packsView },
  { id: "characters", label: "Characters", icon: "roster", view: rosterView },
  { id: "apps", label: "Discord apps", icon: "apps", view: appsView },
  { id: "access", label: "Access & features", icon: "access", view: accessView },
  { id: "releases", label: "Releases", icon: "releases", view: releasesView },
  { id: "audit", label: "Audit log", icon: "audit", view: auditView },
  { id: "settings", label: "Settings", icon: "settings", view: settingsView },
];

export interface ShellDeps {
  win: Window;
  api: AdminApi;
  storage: Storage | null;
  /** Defaults to the page's own host name. */
  hostname?: string;
  now?: () => number;
  copy?: (text: string) => Promise<boolean>;
  download?: (fileName: string, data: unknown) => void;
}

function brandMark(): SVGSVGElement {
  return icon("mark", "brand-mark");
}

function defaultCopy(win: Window): (text: string) => Promise<boolean> {
  return async (text) => {
    try {
      await win.navigator.clipboard.writeText(text);
      return true;
    } catch (error) {
      // The browser refused (no permission, not focused, or no clipboard API). The caller tells the owner to copy by hand.
      if (error instanceof Error) return false;
      throw error;
    }
  };
}

function defaultDownload(win: Window): (fileName: string, data: unknown) => void {
  return (fileName, data) => {
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const link = win.document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.rel = "noopener";
    win.document.body.appendChild(link);
    link.click();
    link.remove();
    win.setTimeout(() => URL.revokeObjectURL(url), 10_000);
  };
}

export function mountShell(root: HTMLElement, deps: ShellDeps): { navigate: (id: string) => void } {
  const { win } = deps;
  const doc = win.document;
  const environment = environmentFor(deps.hostname ?? win.location.hostname);
  doc.documentElement.dataset["environment"] = environment;

  // Theme
  let theme: ThemeChoice = readThemeChoice(deps.storage);
  applyTheme(doc.documentElement, theme);
  const themeSelect = el(
    "select",
    { className: "input select select-compact", attrs: { id: "theme-choice" } },
    ...(["system", "dark", "light"] as const).map((choice) => el("option", { text: choice === "system" ? "System" : choice === "dark" ? "Dark" : "Light", attrs: { value: choice, selected: choice === theme } })),
  );
  themeSelect.addEventListener("change", () => {
    theme = themeSelect.value as ThemeChoice;
    applyTheme(doc.documentElement, theme);
    saveThemeChoice(deps.storage, theme);
  });

  // Banners and status line
  const sessionBanner = el("div", { className: "global-banner-slot" });
  const offlineBanner = el("div", { className: "global-banner-slot" });
  const status = el("div", { className: "toast", attrs: { role: "status", "aria-live": "polite" } });
  let statusTimer: number | undefined;
  const announce = (text: string): void => {
    status.textContent = text;
    status.classList.add("toast-visible");
    if (statusTimer !== undefined) win.clearTimeout(statusTimer);
    statusTimer = win.setTimeout(() => status.classList.remove("toast-visible"), 6000);
  };

  let sessionShown = false;
  const reload = (): void => win.location.reload();
  const reportError = (error: unknown): void => {
    if (!isSessionError(error) || sessionShown) return;
    sessionShown = true;
    replaceChildren(
      sessionBanner,
      el(
        "div",
        { className: "global-banner global-banner-danger", attrs: { role: "alert" } },
        icon("alert"),
        el("p", { className: "global-banner-text", text: "Your Cloudflare Access session expired. Reload the page to sign in again; nothing more can be loaded or saved until then." }),
        button("Reload page", { variant: "primary", small: true, icon: "refresh", onClick: reload }),
      ),
    );
  };

  const showOffline = (offline: boolean): void => {
    replaceChildren(
      offlineBanner,
      offline
        ? el(
            "div",
            { className: "global-banner global-banner-warning", attrs: { role: "status" } },
            icon("alert"),
            el("p", { className: "global-banner-text", text: "You're offline. The panel cannot load or save anything until the connection is back." }),
          )
        : null,
    );
  };
  win.addEventListener("offline", () => showOffline(true));
  win.addEventListener("online", () => showOffline(false));
  showOffline(win.navigator.onLine === false);

  // Leaving a section (or the page) while new invite codes are on screen would lose them.
  let leaveGuard = false;
  const beforeUnload = (event: BeforeUnloadEvent): void => {
    event.preventDefault();
  };
  const setLeaveGuard = (active: boolean): void => {
    if (active === leaveGuard) return;
    leaveGuard = active;
    if (active) win.addEventListener("beforeunload", beforeUnload);
    else win.removeEventListener("beforeunload", beforeUnload);
  };

  const ctx: ViewContext = {
    api: deps.api,
    doc,
    environment,
    now: deps.now ?? (() => Date.now()),
    announce,
    confirm: (options) => confirmDialog(doc, options),
    copy: deps.copy ?? defaultCopy(win),
    download: deps.download ?? defaultDownload(win),
    reload,
    reportError,
    setLeaveGuard,
  };

  // Layout
  const main = el("main", { className: "main", attrs: { id: "main", tabindex: -1 } });
  const navLinks = SECTIONS.map((section) => {
    const link = routeLink(`#/${section.id}`, "", "nav-link");
    link.append(icon(section.icon), el("span", { text: section.label }));
    link.dataset["section"] = section.id;
    return link;
  });
  const skip = button("Skip to content", { variant: "primary", small: true, onClick: () => main.focus() });
  skip.classList.add("skip-link");

  const envBadge = el("span", { className: `env-badge env-${environment}`, text: environment === "staging" ? "Staging" : "Production" });
  const topbar = el(
    "div",
    { className: "topbar" },
    el("div", { className: "brand" }, brandMark(), el("span", { className: "brand-name", text: "DBD Presence" }), el("span", { className: "brand-sep", attrs: { "aria-hidden": "true" }, text: "/" }), el("span", { className: "brand-product", text: "Owner Panel" }), envBadge),
    el("div", { className: "topbar-tools" }, el("label", { className: "theme-label", text: "Theme", attrs: { for: "theme-choice" } }), themeSelect),
  );
  const header = el(
    "header",
    { className: "site-header" },
    environment === "staging"
      ? el("div", { className: "env-banner", attrs: { role: "note", "aria-label": "Environment" } }, el("strong", { text: "Staging" }), el("span", { text: " · Test environment with its own data. Changes here never affect production users." }))
      : null,
    sessionBanner,
    offlineBanner,
    topbar,
  );

  replaceChildren(
    root,
    skip,
    header,
    el("div", { className: "layout" }, el("nav", { className: "sidenav", attrs: { "aria-label": "Sections" } }, el("ul", { className: "nav-list" }, ...navLinks.map((l) => el("li", {}, l)))), main),
    status,
  );

  // Routing
  let current: string | null = null;
  const sectionFor = (hash: string): Section => SECTIONS.find((s) => `#/${s.id}` === hash) ?? SECTIONS[0]!;

  const show = (section: Section, moveFocus: boolean): void => {
    current = section.id;
    for (const link of navLinks) {
      if (link.dataset["section"] === section.id) link.setAttribute("aria-current", "page");
      else link.removeAttribute("aria-current");
    }
    doc.title = `${section.label} · Owner Panel · DBD Presence`;
    setLeaveGuard(false);
    section.view.render(main, ctx);
    if (moveFocus) main.querySelector<HTMLElement>("#page-title")?.focus();
  };

  const onHashChange = async (): Promise<void> => {
    const target = sectionFor(win.location.hash);
    if (target.id === current) return;
    if (leaveGuard && current !== null) {
      const previous = current;
      const ok = await ctx.confirm({
        title: "Leave without copying the codes?",
        body: ["The new invite codes on this page cannot be shown again. A lost code can only be revoked and replaced."],
        confirmLabel: "Leave this page",
        cancelLabel: "Stay",
      });
      if (!ok) {
        win.history.replaceState(null, "", `#/${previous}`);
        return;
      }
    }
    show(target, true);
  };
  win.addEventListener("hashchange", () => void onHashChange());

  const initial = sectionFor(win.location.hash);
  if (win.location.hash !== `#/${initial.id}`) win.history.replaceState(null, "", `#/${initial.id}`);
  show(initial, false);

  return {
    navigate: (id: string) => {
      win.location.hash = `#/${id}`;
    },
  };
}
