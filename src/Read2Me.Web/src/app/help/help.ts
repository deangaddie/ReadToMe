import type { PipelineStepId } from '@app/api/dtos';
import { html } from 'lit-html';
import { unsafeHTML } from 'lit-html/directives/unsafe-html.js';
import { HELP } from './help.generated';

/**
 * `r2m-help`: one Pipeline step's help text, from the module `bun run help` generates out of
 * `docs/guide/<step>.md`. The HTML is trusted because the build rendered it from repo markdown with
 * raw HTML disabled and linted the links (scripts/help-render.ts); nothing user-supplied reaches it.
 * Shown from the step's Overview row only.
 */
export function helpText(step: PipelineStepId) {
  const entry = HELP[step];
  return html`<section class="r2m-help" data-step=${step} aria-labelledby="r2m-help-title-${step}">
    <h2 class="r2m-help__title" id="r2m-help-title-${step}">${entry.title}</h2>
    <div class="r2m-help__body">${unsafeHTML(entry.html)}</div>
  </section>`;
}
