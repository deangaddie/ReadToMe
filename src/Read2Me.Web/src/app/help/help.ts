import type { PipelineStepId } from '@app/api/dtos';
import { html, nothing } from 'lit-html';
import { unsafeHTML } from 'lit-html/directives/unsafe-html.js';
import { HELP } from './help.generated';

/**
 * `r2m-help`: one Pipeline step's help text, from the module `bun run help` generates out of
 * `docs/guide/<step>.md`. The HTML is trusted because the build rendered it from repo markdown with
 * raw HTML disabled and linted the links (scripts/help-render.ts); nothing user-supplied reaches it.
 * Shown from the step's Overview row only. `title: false` leaves out the heading for a host that
 * draws its own (the Overview's help panel), which then labels the section itself.
 */
export function helpText(step: PipelineStepId, options: { title?: boolean } = {}) {
  const entry = HELP[step];
  const titled = options.title ?? true;
  return html`<section
    class="r2m-help"
    data-step=${step}
    aria-labelledby=${titled ? `r2m-help-title-${step}` : nothing}
  >
    ${
      titled
        ? html`<h2 class="r2m-help__title" id="r2m-help-title-${step}">${entry.title}</h2>`
        : nothing
    }
    <div class="r2m-help__body">${unsafeHTML(entry.html)}</div>
  </section>`;
}
