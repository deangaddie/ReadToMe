import { PromptCatalogEntry } from '@app/api';

/**
 * Pure editing rules of the prompts page (ticket 23): a draft is compared with the *resolved*
 * template (override or default), a token chip inserts at the textarea caret, and Reset is offered
 * whenever there is an override to drop or an edit to discard.
 */

/** `book_title` → `{{book_title}}`, the exact literal `PromptTemplates.Render` replaces. */
export function tokenLiteral(token: string): string {
  return `{{${token}}}`;
}

export interface TokenInsertion {
  text: string;
  /** Where the caret lands: right after the inserted token. */
  caret: number;
}

/**
 * Inserts the token literal at the selection `[start, end)`, replacing any selected text. A null
 * start (no caret known) appends; positions are clamped and may be given in either order.
 */
export function insertToken(
  text: string,
  token: string,
  start: number | null,
  end: number | null = start,
): TokenInsertion {
  const clamp = (n: number) => Math.min(Math.max(n, 0), text.length);
  const from = start === null ? text.length : clamp(start);
  const to = end === null ? from : clamp(end);
  const [lo, hi] = from <= to ? [from, to] : [to, from];
  const literal = tokenLiteral(token);
  return { text: text.slice(0, lo) + literal + text.slice(hi), caret: lo + literal.length };
}

/** Unsaved when the draft differs from what the host currently resolves for the kind. */
export function isDirty(entry: PromptCatalogEntry, draft: string): boolean {
  return draft !== entry.template;
}

/** Reset drops a stored override or discards unsaved edits; nothing to do otherwise. */
export function canReset(entry: PromptCatalogEntry, draft: string): boolean {
  return entry.isOverridden || draft !== entry.defaultTemplate;
}
