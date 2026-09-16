import { ContextParagraphDto, MAX_CONTEXT_WINDOW } from '@app/api';

/**
 * The line-context window (research §4 "Lines"): opens at 3 before / 2 after, "+ previous" grows
 * the top by 3 and "+ next" the bottom by 2, each capped at the host's 10.
 */
export const INITIAL_BEFORE = 3;
export const INITIAL_AFTER = 2;
export const BEFORE_STEP = 3;
export const AFTER_STEP = 2;

export interface ContextWindow {
  before: number;
  after: number;
}

export const INITIAL_WINDOW: ContextWindow = { before: INITIAL_BEFORE, after: INITIAL_AFTER };

export function growBefore(window: ContextWindow): ContextWindow {
  return { ...window, before: Math.min(window.before + BEFORE_STEP, MAX_CONTEXT_WINDOW) };
}

export function growAfter(window: ContextWindow): ContextWindow {
  return { ...window, after: Math.min(window.after + AFTER_STEP, MAX_CONTEXT_WINDOW) };
}

export function canGrowBefore(window: ContextWindow): boolean {
  return window.before < MAX_CONTEXT_WINDOW;
}

export function canGrowAfter(window: ContextWindow): boolean {
  return window.after < MAX_CONTEXT_WINDOW;
}

/** A speaker chip on a context paragraph; `unknown` is a dialog item not yet attributed. */
export interface ContextSpeaker {
  name: string;
  unknown: boolean;
}

/**
 * The distinct speakers of a paragraph's dialog items, in order of first appearance. Narration is
 * not a speaker, so an all-narration paragraph has none.
 */
export function contextSpeakers(paragraph: ContextParagraphDto): ContextSpeaker[] {
  const seen = new Set<string>();
  const speakers: ContextSpeaker[] = [];
  for (const item of paragraph.items) {
    if (!item.isDialog) continue;
    const key = item.speaker ?? '';
    if (seen.has(key)) continue;
    seen.add(key);
    speakers.push(item.speaker ? { name: item.speaker, unknown: false } : { name: '?', unknown: true });
  }
  return speakers;
}
