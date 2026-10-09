import { html, nothing } from 'lit-html';
import type { NarratorDto } from '@app/api';
import { icon } from '@app/ui/partials';

/**
 * What the detail shows when the Narrator row is selected while the book is narrated by a
 * character (research §4 "Narrator signpost"): where narration is really edited, a button to go
 * there, and the seed Narrator's own voices, kept unused until the link is removed. Stateless, so
 * a partial; the page styles `.narrator-signpost` in `cast.css`.
 */
export function narratorSignpost(options: {
  narrator: NarratorDto;
  /** The seed Narrator row's own voice names. */
  unusedVoices: readonly string[];
  onGoToLinked: () => void;
}) {
  const { narrator, unusedVoices, onGoToLinked } = options;
  const name = narrator.displayName;
  return html`<div class="narrator-signpost" data-testid="narrator-signpost">
    <h2 class="narrator-signpost__title">${icon('link')} Narrator → ${name}</h2>
    <p class="narrator-signpost__info">
      Narration in this book is spoken by ${name}. Voices and voice rules are edited on ${name}.
    </p>
    <button type="button" class="r2m-button r2m-button--filled" @click=${onGoToLinked}>
      ${icon('arrow_forward')} Go to ${name}
    </button>
    ${
      unusedVoices.length > 0
        ? html`<details class="narrator-signpost__unused">
            <summary>
              ${unusedVoices.length} unused narrator
              ${unusedVoices.length === 1 ? 'voice' : 'voices'}
            </summary>
            <p class="narrator-signpost__hint">
              These voices are kept safely and return when the narrator link is removed.
            </p>
            <ul>
              ${unusedVoices.map((v) => html`<li>${v}</li>`)}
            </ul>
          </details>`
        : nothing
    }
  </div>`;
}
