// What every section receives from the shell. Browser services are injected so tests can drive them.

import type { AdminApi } from "./api.ts";
import type { ConfirmOptions } from "./dialog.ts";
import type { Environment } from "./env.ts";

export interface ViewContext {
  api: AdminApi;
  doc: Document;
  /** Which deployment this is; selects the content host images may come from (src/cdn.ts). */
  environment: Environment;
  now: () => number;
  /** Polite status message for screen readers and the visible status line. */
  announce: (text: string) => void;
  confirm: (options: ConfirmOptions) => Promise<boolean>;
  /** Copies text; false when the browser refused (the code stays on screen to copy by hand). */
  copy: (text: string) => Promise<boolean>;
  /** Offers a JSON document as a file download. */
  download: (fileName: string, data: unknown) => void;
  reload: () => void;
  /** Lets the shell show its global banner once a call reports an expired session. */
  reportError: (error: unknown) => void;
  /** Warns before leaving the page while something would be lost (new invite codes on screen). */
  setLeaveGuard: (active: boolean) => void;
}

export interface View {
  /** Builds the section into `container`; called on every visit. */
  render: (container: HTMLElement, ctx: ViewContext) => void;
}
