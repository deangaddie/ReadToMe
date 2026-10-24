import { html, nothing } from 'lit-html';
import { keyed } from 'lit-html/directives/keyed.js';
import { repeat } from 'lit-html/directives/repeat.js';
import type { Guid } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { icon } from '@app/ui/partials';
import type { PronunciationEditor } from '../pronunciations/pronunciation-editor';
import { type IndexedMarks, NO_MARKS } from './pronunciation-marks';
import { panelRows } from './pronunciation-panel-logic';
import type { PronunciationPanelState } from './pronunciation-panel-store';
import css from './pronunciation-panel.css' with { type: 'text' };
import '../pronunciations/pronunciation-editor';

adoptStyles(css);

/**
 * The Book view's Pronunciations panel (pronunciation ticket 16, variant D): the third body column
 * beside the reader (floating over its right edge on a narrow page). It lists the entries the
 * loaded chapters match, each with its count there and the items with audio, a hover lighting its
 * marks, and links to the whole list; a row, a mark's Edit pencil or the selection pill opens the
 * shared {@link PronunciationEditor} at the top, its try-out the sentence it was opened from. The
 * page's {@link PronunciationPanelState} holds what is open. Closed, the panel is only hidden, so
 * an unsaved draft is still there when it opens again.
 */
export class PronunciationPanel extends R2mElement {
  readonly #state = signal<PronunciationPanelState | null>(null);
  get state() {
    return this.#state();
  }
  set state(value: PronunciationPanelState | null) {
    this.#state.set(value);
  }

  readonly #folder = signal<string | null>(null);
  get folder() {
    return this.#folder();
  }
  set folder(value: string | null) {
    this.#folder.set(value);
  }

  readonly #marks = signal<IndexedMarks>(NO_MARKS);
  get marks() {
    return this.#marks();
  }
  /** The loaded chapters' marks: the rows. */
  set marks(value: IndexedMarks) {
    this.#marks.set(value);
  }

  readonly #itemsWithAudio = signal<ReadonlySet<Guid>>(new Set());
  get itemsWithAudio() {
    return this.#itemsWithAudio();
  }
  /** The loaded items that have audio: each row's with-audio count. */
  set itemsWithAudio(value: ReadonlySet<Guid>) {
    this.#itemsWithAudio.set(value);
  }

  readonly #readOnly = signal<string | null>(null);
  get readOnly() {
    return this.#readOnly();
  }
  /** `StepView.readOnly`, for the editor. */
  set readOnly(value: string | null) {
    this.#readOnly.set(value);
  }

  readonly #audioInFlight = signal(0);
  get audioInFlight() {
    return this.#audioInFlight();
  }
  /** Items queued or processing for audio, for the editor. */
  set audioInFlight(value: number) {
    this.#audioInFlight.set(value);
  }

  protected override connected(): void {
    this.classList.add('pron-panel');
    this.setAttribute('role', 'complementary');
    this.setAttribute('aria-label', 'Pronunciations');
    this.effect(() => {
      this.hidden = !this.#state()?.open();
    });
  }

  /** The editor holds changes not saved yet. */
  dirty(): boolean {
    return this.querySelector<PronunciationEditor>('r2m-pronunciation-editor')?.dirty() === true;
  }

  protected template() {
    const state = this.#state();
    const folder = this.#folder();
    if (!state || !folder) return nothing;
    const session = state.session();
    const entries = state.entries();
    const withAudio = this.#itemsWithAudio();
    const rows = panelRows(this.#marks(), (id) => withAudio.has(id));
    const editing = session?.entryId ?? null;
    const entry = editing === null ? null : (entries.find((e) => e.id === editing) ?? null);
    return html`
      <header class="pron-panel__head">
        <h2 class="pron-panel__title">Pronunciations</h2>
        <button
          type="button"
          class="r2m-icon-button"
          data-action="close-panel"
          aria-label="Close Pronunciations"
          data-tooltip="Close"
          @click=${() => state.hide()}
        >
          ${icon('close')}
        </button>
      </header>
      ${
        session && (session.entryId === null || entry)
          ? keyed(
              session.key,
              html`<r2m-pronunciation-editor
                class="pron-panel__editor"
                .folder=${folder}
                .entry=${entry}
                .prefill=${session.forms}
                .origin=${session.origin}
                .readOnly=${this.#readOnly()}
                .audioInFlight=${this.#audioInFlight()}
                @editor-close=${() => state.endSession()}
              ></r2m-pronunciation-editor>`,
            )
          : html`<p class="pron-panel__hint">Select a word in the book to add one.</p>`
      }
      <h3 class="pron-panel__subtitle">In the loaded chapters</h3>
      ${
        rows.length === 0
          ? html`<p class="pron-panel__hint">None of the list occurs here.</p>`
          : html`<ul class="pron-panel__list">
              ${repeat(
                rows,
                (r) => r.entry.id,
                (r) =>
                  html`<li
                    class="pron-panel__item ${r.entry.id === editing ? 'pron-panel__item--open' : ''}"
                    data-role="row"
                    data-id=${r.entry.id}
                    @pointerenter=${() => state.hovered.set(r.entry.id)}
                    @pointerleave=${() => state.hovered.set(null)}
                  >
                    <button
                      type="button"
                      class="pron-panel__row"
                      aria-label="Edit ${r.entry.writtenForm}"
                      @click=${() => void state.edit(r.entry.id, r.first)}
                    >
                      <span class="pron-panel__written">${r.entry.writtenForm}</span>
                      ${icon('arrow_forward', 'pron-panel__arrow')}
                      <span class="pron-panel__spoken">${r.entry.spokenForm}</span>
                      <span class="pron-panel__count">${r.count} · ${r.withAudio} audio</span>
                    </button>
                  </li>`,
              )}
            </ul>`
      }
      <a
        class="pron-panel__all"
        data-role="all"
        href=${`projects/${encodeURIComponent(folder)}/pronunciations`}
        >All ${entries.length} on the Pronunciations page ${icon('open_in_new')}</a
      >
    `;
  }
}
define('r2m-pronunciation-panel', PronunciationPanel);
