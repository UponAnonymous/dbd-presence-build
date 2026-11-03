// Types for scripts/legal-render.mjs (used by test/legal.test.ts).
export function escapeHtml(text: string): string;
export function safeHref(target: string): string | null;
export function renderInline(text: string, allowLinks?: boolean): string;
export function renderMarkdown(markdown: string): { title: string; html: string };
export function renderLegalModule(privacyMarkdown: string, termsMarkdown: string): string;
