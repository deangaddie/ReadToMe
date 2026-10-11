import { html, nothing } from 'lit-html';
import { spinner } from '@app/ui/partials';

/** What a card shows of its draft: the `CardState` signals the page keeps per card. */
export interface AudioCardState {
  dirty(): boolean;
  saving(): boolean;
  /** The card's validation message; Save is disabled while set. */
  error(): string | null;
}

export interface AudioCardOptions {
  /** Stable id for tests and the E2E driver (`data-card`). */
  card: string;
  title: string;
  blurb?: string;
  state: AudioCardState;
  onSave: () => void;
  /** Extra buttons beside Save (the ffmpeg card's Test). */
  actions?: unknown;
}

/**
 * One independently saved card of the audio-processing page (ticket 24): title, blurb, the
 * caller's fields, then a footer with the validation message, extra actions and a Save gated on
 * dirty + valid. A partial: it holds no state of its own, it reads the card's.
 */
export function audioCard(o: AudioCardOptions, body: unknown) {
  const dirty = o.state.dirty();
  const saving = o.state.saving();
  const error = o.state.error();
  return html`<section class="audio-card" data-card=${o.card}>
    <header class="audio-card__head">
      <h2 class="audio-card__title">${o.title}</h2>
      ${dirty ? html`<span class="audio-card__dirty" role="status">Unsaved changes</span>` : nothing}
    </header>
    ${o.blurb ? html`<p class="audio-card__blurb">${o.blurb}</p>` : nothing}
    <div class="audio-card__body">${body}</div>
    <footer class="audio-card__foot">
      ${
        error
          ? html`<span class="audio-card__error" role="alert" data-role="card-error"
              >${error}</span
            >`
          : nothing
      }
      <span class="audio-card__actions">
        ${o.actions ?? nothing}
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="save"
          ?disabled=${!dirty || !!error || saving}
          @click=${o.onSave}
        >
          ${saving ? html`${spinner(16)} Saving…` : 'Save'}
        </button>
      </span>
    </footer>
  </section>`;
}
