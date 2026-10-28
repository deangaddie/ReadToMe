import { html, nothing } from 'lit-html';
import type { SetupFixHint } from '@app/api';
import { icon } from '@app/ui/partials';

/**
 * A check's fix hint: the sentence, then a link to the Settings section that owns the value and/or
 * a copyable command or file path (a button that hands it to `onCopy`).
 */
export function fixHint(fix: SetupFixHint, onCopy: (text: string) => void) {
  const { link, copy } = fix;
  return html`<span class="setup-check__fix">
    <span class="setup-check__fix-text">${fix.text}</span>
    ${link ? html`<a class="setup-check__fix-link" href=${link.path}>${icon('open_in_new')} ${link.label}</a>` : nothing}
    ${
      copy
        ? html`<button
            type="button"
            class="setup-check__fix-copy"
            data-action="copy"
            aria-label=${`Copy ${copy}`}
            data-tooltip="Copy"
            @click=${() => onCopy(copy)}
          >
            <code>${copy}</code>${icon('content_copy')}
          </button>`
        : nothing
    }
  </span>`;
}
