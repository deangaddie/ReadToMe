import { html } from 'lit-html';
import type { LlmStreamFeed } from '@app/activity/stream-feed';
import { icon } from './partials';
import './stream-llm';

export interface StreamDisclosureOptions {
  /** The feed whose events the stream shows; the element stays mounted while hidden, so nothing is missed. */
  feed: LlmStreamFeed;
  open: boolean;
  onToggle: () => void;
  /** Extra classes on the root, for the host's layout (`margin-top: auto` in a full-screen dialog). */
  cls?: string;
}

/**
 * The collapsible "Show / Hide AI activity" strip at the foot of an AI dialog: a toggle
 * (`data-action="toggle-stream"`, `aria-expanded`) over an `<r2m-stream-llm>` that is always
 * mounted, so it captures every request made while the dialog is open.
 */
export function streamDisclosure({ feed, open, onToggle, cls = '' }: StreamDisclosureOptions) {
  return html`<div class="r2m-stream-disclosure ${cls}">
    <button
      type="button"
      class="r2m-button r2m-stream-disclosure__toggle"
      data-action="toggle-stream"
      aria-expanded=${open ? 'true' : 'false'}
      @click=${onToggle}
    >
      ${icon(open ? 'expand_more' : 'expand_less')}
      ${open ? 'Hide AI activity' : 'Show AI activity'}
    </button>
    <div class="r2m-stream-disclosure__body ${open ? 'r2m-stream-disclosure__body--open' : ''}">
      <r2m-stream-llm .events=${feed.events()} .maxTurns=${feed.maxUnits}></r2m-stream-llm>
    </div>
  </div>`;
}
