// Entry point: mounts the shell with the real browser services. Loaded as an external module (no inline script).

import { AdminApi } from "./api.ts";
import { mountShell } from "./shell.ts";
import { safeLocalStorage } from "./theme.ts";

function start(): void {
  const root = document.getElementById("app");
  if (!root) return;
  const api = new AdminApi(
    (input, init) => window.fetch(input, init),
    () => window.navigator.onLine,
  );
  mountShell(root, { win: window, api, storage: safeLocalStorage(window) });
}

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
else start();
