// Text-only DOM building (FR-ADM-04). Every value reaches the page through `textContent` or a Text node; there is no
// HTML parsing anywhere in the panel. Attributes are set one by one, and event-handler, style and URL-bearing
// attributes are refused so user data can never become markup, script or a request. scripts/check-dom-sinks.mjs fails
// the build on any HTML-parsing API.

import { isPackImageUrl, isUploadImageUrl } from "./cdn.ts";
import type { Environment } from "./env.ts";

export type Child = Node | string | null | undefined | false;

export interface ElementOptions {
  className?: string;
  text?: string;
  attrs?: Record<string, string | number | boolean | null | undefined>;
  on?: { [K in keyof HTMLElementEventMap]?: (event: HTMLElementEventMap[K]) => void };
}

/** Attributes that could run script, load a resource or restyle the page; never set through `el`. */
const BLOCKED_ATTRIBUTE = /^(on|style$|src(set|doc)?$|href$|action$|formaction$|xlink:href$|background$|poster$|data$)/i;

function append(parent: Node, children: readonly Child[]): void {
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    parent.appendChild(typeof child === "string" ? document.createTextNode(child) : child);
  }
}

/** Creates an element; strings become Text nodes. */
export function el<K extends keyof HTMLElementTagNameMap>(tag: K, options: ElementOptions = {}, ...children: Child[]): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (options.className) node.className = options.className;
  if (options.text !== undefined) node.textContent = options.text;
  for (const [name, value] of Object.entries(options.attrs ?? {})) {
    if (BLOCKED_ATTRIBUTE.test(name)) throw new Error(`attribute not allowed: ${name}`);
    if (value === null || value === undefined || value === false) continue;
    node.setAttribute(name, value === true ? "" : String(value));
  }
  for (const [type, handler] of Object.entries(options.on ?? {})) {
    node.addEventListener(type, handler as EventListener);
  }
  append(node, children);
  return node;
}

/** Replaces all children of `parent`. */
export function replaceChildren(parent: Element, ...children: Child[]): void {
  while (parent.firstChild) parent.removeChild(parent.firstChild);
  append(parent, children);
}

/** An in-page link to a panel route (`#/section`). Only fixed, internal fragments are accepted. */
export function routeLink(route: string, label: string, className?: string): HTMLAnchorElement {
  if (!/^#\/[a-z-]*$/.test(route)) throw new Error(`not a panel route: ${route}`);
  const link = document.createElement("a");
  link.setAttribute("href", route);
  if (className) link.className = className;
  link.textContent = label;
  return link;
}

/**
 * An image from the content host. The address must pass `isPackImageUrl` for this environment (src/cdn.ts); anything
 * else throws, so no other address can ever be loaded. No referrer is sent.
 */
export function packImage(url: string, environment: Environment, alt: string, size: number): HTMLImageElement {
  if (!isPackImageUrl(url, environment)) throw new Error("not a pack image address");
  return contentImage(url, alt, size);
}

/** A user upload from the content host; the address must pass `isUploadImageUrl` for this environment, or this throws. */
export function uploadImage(url: string, environment: Environment, alt: string, size: number): HTMLImageElement {
  if (!isUploadImageUrl(url, environment)) throw new Error("not an upload address");
  return contentImage(url, alt, size);
}

function contentImage(url: string, alt: string, size: number): HTMLImageElement {
  const img = document.createElement("img");
  img.setAttribute("alt", alt);
  img.setAttribute("width", String(size));
  img.setAttribute("height", String(size));
  img.setAttribute("loading", "lazy");
  img.setAttribute("decoding", "async");
  img.setAttribute("referrerpolicy", "no-referrer");
  img.setAttribute("src", url);
  return img;
}

const SVG_NS = "http://www.w3.org/2000/svg";

/** Fixed icon shapes (24-unit grid). Decorative: hidden from assistive technology; text always carries the meaning. */
const ICONS = {
  mark: ["M12 3.5 20.5 12 12 20.5 3.5 12Z", "M12 6.6 17.4 12 12 17.4 6.6 12Z", "M12 9.4 14.6 12 12 14.6 9.4 12Z"],
  invites: ["M4 7h16v10H4z", "M4 7l8 6 8-6"],
  users: ["M9 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7z", "M2.5 20c.6-3.4 3.2-5.5 6.5-5.5s5.9 2.1 6.5 5.5", "M16 4.3a3.5 3.5 0 0 1 0 6.4", "M18 14.8c1.8.8 3 2.6 3.5 5.2"],
  audit: ["M7 3.5h8l4 4v13H7z", "M15 3.5v4h4", "M10 12h6", "M10 16h6"],
  access: ["M6 11V8a6 6 0 0 1 12 0v3", "M5 11h14v10H5z", "M12 15v2"],
  check: ["M5 12.5l4.5 4.5L19 7.5"],
  alert: ["M12 3.5 21.5 20h-19z", "M12 10v4.5", "M12 17.2v.3"],
  info: ["M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z", "M12 11v5.5", "M12 7.8v.3"],
  copy: ["M9 9h11v11H9z", "M5 15H4V4h11v1"],
  chevron: ["M9 6l6 6-6 6"],
  refresh: ["M20 11a8 8 0 1 0-2.3 5.7", "M20 4.5V11h-6.5"],
  download: ["M12 4v11", "M7 10.5l5 5 5-5", "M5 20h14"],
  packs: ["M4 4h7v7H4z", "M13 4h7v7h-7z", "M4 13h7v7H4z", "M13 13h7v7h-7z"],
  apps: ["M4 6.5h16v11H4z", "M8 21h8", "M12 17.5V21"],
  upload: ["M12 20V9", "M7 13.5l5-5 5 5", "M5 4h14"],
  plus: ["M12 5v14", "M5 12h14"],
  back: ["M15 6l-6 6 6 6"],
  trash: ["M5 7h14", "M9 7V4.5h6V7", "M7 7l1 13h8l1-13"],
  image: ["M4 5h16v14H4z", "M4 16l5-5 4 4 2.5-2.5L20 17", "M15.5 9.5v.2"],
  uploads: ["M4 5h16v14H4z", "M4 16l5-5 4 4 2.5-2.5L20 17", "M12 3v5", "M9.5 5.5 12 3l2.5 2.5"],
  releases: ["M4 8l8-4 8 4v8l-8 4-8-4z", "M4 8l8 4 8-4", "M12 12v8"],
  dashboard: ["M4 4h7v9H4z", "M13 4h7v5h-7z", "M13 11h7v9h-7z", "M4 15h7v5H4z"],
  settings: ["M4 7h10", "M18 7h2", "M16 5v4", "M4 17h2", "M10 17h10", "M8 15v4"],
  roster: ["M12 3.5a4.5 4.5 0 0 1 4.5 4.5v2a4.5 4.5 0 0 1-9 0V8A4.5 4.5 0 0 1 12 3.5z", "M10 9.5v.2", "M14 9.5v.2", "M4 21c1-3.6 4.1-5.5 8-5.5s7 1.9 8 5.5"],
} as const;

export type IconName = keyof typeof ICONS;

export function icon(name: IconName, className = "icon"): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("class", className);
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  for (const d of ICONS[name]) {
    const path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", d);
    svg.appendChild(path);
  }
  return svg;
}
