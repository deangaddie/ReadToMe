import { html } from 'lit-html';
import type { LlmStreamFeed } from '@app/activity/stream-feed';
import { icon } from './partials';
import './stream-llm';

export interface StreamDisclosureOptions {
  /** The body's id, which the toggle names in `aria-controls`; one per dialog. */
  id: string;
  /** The feed whose events the stream shows; the element stays mounted while closed, so nothing is missed. */
  feed: LlmStreamFeed;
  open: boolean;
  onToggle: () => void;
  /** Extra classes on the root, for the host's layout (`margin-top: auto` in a full-screen dialog). */
  cls?: string;
}

/**
 * The collapsible "Show / Hide AI activity" strip at the foot of an AI dialog: a toggle
 * (`data-action="toggle-stream"`, `aria-expanded`, `aria-controls`) over an `<r2m-stream-llm>`
 * that is always mounted, so it captures every request made while the dialog is open. Closed, CSS
 * skips the body (`content-visibility: hidden`).
 */
export function streamDisclosure({ id, feed, open, onToggle, cls = '' }: StreamDisclosureOptions) {
  return html`<div class="r2m-stream-disclosure ${cls}">
    <button
      type="button"
      class="r2m-button r2m-stream-disclosure__toggle"
      data-action="toggle-stream"
      aria-expanded=${open ? 'true' : 'false'}
      aria-controls=${id}
      @click=${onToggle}
    >
      ${icon(open ? 'expand_more' : 'expand_less')}
      ${open ? 'Hide AI activity' : 'Show AI activity'}
    </button>
    <div
      id=${id}
      class="r2m-stream-disclosure__body ${open ? 'r2m-stream-disclosure__body--open' : ''}"
    >
      <r2m-stream-llm
        .events=${feed.events()}
        .maxTurns=${feed.maxUnits}
        .shown=${open}
      ></r2m-stream-llm>
    </div>
  </div>`;
}
