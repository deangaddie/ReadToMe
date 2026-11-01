import { html } from 'lit-html';
import { helpText } from '@app/help/help';
import type { IconName } from '@app/ui/icons';
import { icon } from '@app/ui/partials';
import type { StepId } from './pipeline-steps';

/** The guide's folder on GitHub; `/<step>.md` is the page `r2m-help` was rendered from. */
export const GUIDE_URL = 'https://github.com/deangaddie/ReadToMe/blob/main/docs/guide';

/**
 * One Pipeline step's help as the Overview shows it (guidance-primitives spec §3): the step's icon
 * and name, a close ×, the `r2m-help` body and an "Open the guide" link to the source page. The
 * same template serves both placements, the right column at ≥ 1100 px (`side`) and inside the
 * step's row below that (`inline`); the Overview's CSS shows one and hides the other, so the help
 * stays open when the window crosses the breakpoint.
 */
export function helpPanel(
  step: { id: StepId; title: string; icon: IconName },
  onClose: () => void,
  placement: 'side' | 'inline',
) {
  return html`<section
    class="overview__help overview__help--${placement}"
    aria-label="Help: ${step.title}"
  >
    <header class="overview__help-head">
      <h2 class="overview__help-title">${icon(step.icon)} ${step.title}</h2>
      <button type="button" class="r2m-icon-button" aria-label="Close help" @click=${onClose}>
        ${icon('close')}
      </button>
    </header>
    ${helpText(step.id, { title: false })}
    <p class="overview__help-foot">
      <a
        class="overview__help-guide"
        href="${GUIDE_URL}/${step.id.toLowerCase()}.md"
        target="_blank"
        rel="noopener"
        >${icon('open_in_new')} Open the guide</a
      >
    </p>
  </section>`;
}
