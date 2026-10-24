import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { icon } from '@app/ui/partials';
import type { TextPlace } from '../pronunciations/try-out-logic';
import { selectedPlace } from './pronunciation-marks';
import { type SelectionPill, selectionPill } from './pronunciation-panel-logic';
import type { PronunciationPanelState } from './pronunciation-panel-store';
import css from './pronunciation-pill.css' with { type: 'text' };

adoptStyles(css);

/** The pill on show: what it offers, where in the book, and the selection's box to sit above. */
interface ShownPill {
  pill: SelectionPill;
  place: TextPlace;
  left: number;
  top: number;
}

/**
 * The Book view's Add from the book (pronunciation ticket 16): a selection of 1–4 words inside one
 * item's text (anywhere in the element's parent) shows an **Add pronunciation** pill just above it,
 * or **Edit pronunciation** when it already is a written form; the pill opens the panel's editor,
 * pre-filled with the selection to add. A top-layer popover, so it sits over the reader's rows.
 * Nothing is offered while a step is viewed.
 */
export class PronunciationPill extends R2mElement {
  readonly #state = signal<PronunciationPanelState | null>(null);
  get state() {
    return this.#state();
  }
  set state(value: PronunciationPanelState | null) {
    this.#state.set(value);
  }

  readonly #readOnly = signal<string | null>(null);
  get readOnly() {
    return this.#readOnly();
  }
  /** `StepView.readOnly`: nothing is offered while a step is viewed. */
  set readOnly(value: string | null) {
    this.#readOnly.set(value);
    if (value !== null) this.shown.set(null);
  }

  readonly shown = signal<ShownPill | null>(null);

  protected override connected(): void {
    this.classList.add('pron-pill');
    const onChange = () => this.readSelection();
    document.addEventListener('selectionchange', onChange);
    this.onDisconnect(() => document.removeEventListener('selectionchange', onChange));
    this.effect(() => {
      const open = this.shown() !== null;
      void this.rendered().then(() => {
        const popover = this.querySelector<HTMLElement>('.pron-pill__popover');
        if (!popover) return;
        if (open && !popover.matches(':popover-open')) popover.showPopover?.();
        if (!open && popover.matches(':popover-open')) popover.hidePopover?.();
      });
    });
  }

  /** Reads the document's selection: the pill shows for one inside an item's text in the parent. */
  readSelection(): void {
    const state = this.#state();
    const selection = document.getSelection();
    const range = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
    const root = this.parentElement;
    const place =
      state && this.#readOnly() === null && range && root?.contains(range.commonAncestorContainer)
        ? selectedPlace(range)
        : null;
    const pill = place ? selectionPill(place.text, state!.entries()) : null;
    if (!place || !pill) {
      this.shown.set(null);
      return;
    }
    const box = range!.getBoundingClientRect();
    this.shown.set({
      pill,
      place: { itemId: place.itemId, offset: place.offset + pill.lead },
      left: box.left + box.width / 2,
      top: box.top,
    });
  }

  #pick(shown: ShownPill): void {
    const state = this.#state();
    this.shown.set(null);
    document.getSelection()?.removeAllRanges();
    if (!state) return;
    const { pill, place } = shown;
    if (pill.entry) void state.edit(pill.entry.id, place);
    else void state.add(pill.forms, place);
  }

  protected template() {
    const shown = this.shown();
    // One popover element throughout: replacing it would drop it from the top layer.
    return html`<div
      class="pron-pill__popover"
      popover="manual"
      style=${shown ? `left: ${shown.left}px; top: ${shown.top}px` : nothing}
    >
      ${
        shown
          ? html`<button
              type="button"
              class="r2m-button r2m-button--filled pron-pill__button"
              data-action="pill"
              @pointerdown=${(e: Event) => e.preventDefault()}
              @click=${() => this.#pick(shown)}
            >
              ${icon(shown.pill.action === 'edit' ? 'edit' : 'add')}
              ${shown.pill.action === 'edit' ? 'Edit pronunciation' : 'Add pronunciation'}
            </button>`
          : nothing
      }
    </div>`;
  }
}
define('r2m-pronunciation-pill', PronunciationPill);
