// Theme choice: System (follows the OS), Dark or Light, as in the desktop app's Appearance page. The choice is kept
// in this browser only; when storage is unavailable the panel simply follows the system.

export type ThemeChoice = "system" | "dark" | "light";

const KEY = "dbdp-panel-theme";

export function readThemeChoice(storage: Storage | null): ThemeChoice {
  try {
    const value = storage?.getItem(KEY);
    return value === "dark" || value === "light" ? value : "system";
  } catch (error) {
    if (error instanceof DOMException) return "system";
    throw error;
  }
}

export function saveThemeChoice(storage: Storage | null, choice: ThemeChoice): void {
  try {
    if (choice === "system") storage?.removeItem(KEY);
    else storage?.setItem(KEY, choice);
  } catch (error) {
    // Storage can be blocked (private window, site data off). The choice then lasts for this page only.
    if (!(error instanceof DOMException)) throw error;
  }
}

/** Sets `data-theme` on the root element; "system" removes it so the `prefers-color-scheme` rules apply. */
export function applyTheme(root: HTMLElement, choice: ThemeChoice): void {
  if (choice === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", choice);
}

export function safeLocalStorage(win: Window): Storage | null {
  try {
    return win.localStorage;
  } catch (error) {
    if (error instanceof DOMException) return null;
    throw error;
  }
}
