import { topmostModal } from '@app/core/dialog';

export type AnnouncePriority = 'polite' | 'assertive';

/**
 * Says `text` to a screen reader without moving focus: the app's one way to announce, so nothing
 * else builds a live region. It calls `ariaNotify` on the topmost modal dialog, because everything
 * outside it is inert and unheard, and on the document when none is open. `assertive` is for data
 * loss, a lost connection or a failure. A browser without `ariaNotify` hears nothing.
 */
export function announce(text: string, priority: AnnouncePriority = 'polite'): void {
  const target = topmostModal() ?? document;
  target.ariaNotify?.(text, { priority: priority === 'assertive' ? 'high' : 'normal' });
}
