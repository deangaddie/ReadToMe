import { html, nothing } from 'lit-html';
import { type Guid, type PronunciationRef, PronunciationsApi } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { blockable, icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import {
  type TextPlace,
  gpuBusyReason,
  playBlockedReason,
  playView,
} from '../pronunciations/try-out-logic';
import { TryOutPlayer } from '../pronunciations/try-out-player';
import { type IndexedMarks, NO_MARKS } from './pronunciation-marks';
import css from './pronunciation-card.css' with { type: 'text' };

adoptStyles(css);

const ANCHOR = '--r2m-pron-card';
/** A pointer resting on a mark this long opens its card; focus opens it at once. */
const SHOW_DELAY_MS = 250;
/** Time to move the pointer from the mark onto the card before it closes. */
const HIDE_DELAY_MS = 200;

/** The mark a card is open for: the item, the sentence a try-out names, and the entry. */
export interface OpenMark {
  anchor: HTMLElement;
  itemId: Guid;
  /** Where the mark starts in the item's text. */
  start: number;
  sentenceStart: number;
  pronunciationId: Guid;
}

/** `pronunciation-edit`: the Edit pencil asks for the entry in the Book view's panel. */
export interface PronunciationEditRequest {
  pronunciationId: Guid;
  /** The mark it was asked from: the sentence the panel's try-out plays. */
  place: TextPlace;
}

/**
 * The Book view's Pronunciation hover card (pronunciation ticket 15, variant D): hovering or
 * focusing a mark (`.r2m-pron-mark`, anywhere in the element's parent) shows `written → spoken`,
 * **▶ This sentence** (a try-out of the mark's sentence in the item's own voice, the list applied)
 * and an Edit pencil that asks (`pronunciation-edit`, off while a step is viewed) for the entry in
 * the Book view's panel. One shared top-layer popover anchored to the mark, as the node menu is.
 * Playing is off with its reason while a step is viewed or a GPU job is queued or running; one
 * sentence plays at a time and keeps playing when the card closes, as a Try it sample does.
 * Enter on a mark moves focus into the card; Escape closes it and gives focus back to the mark.
 */
export class PronunciationCard extends R2mElement {
  private readonly api = use(PronunciationsApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

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
  /** The reader's marks: the entries a card names. */
  set marks(value: IndexedMarks) {
    this.#marks.set(value);
  }

  readonly #readOnly = signal<string | null>(null);
  get readOnly() {
    return this.#readOnly();
  }
  /** `StepView.readOnly`: why nothing plays while a step is viewed, or null. */
  set readOnly(value: string | null) {
    this.#readOnly.set(value);
  }

  readonly #open = signal<OpenMark | null>(null);
  readonly open = this.#open.asReadonly();

  readonly player = new TryOutPlayer({
    render: (request, abort) => this.api.tryOut(this.folder!, request, abort),
    failed: (error) => this.toast.problem(error.toProblem()),
  });

  private readonly blocked = computed(() =>
    playBlockedReason(this.#readOnly(), gpuBusyReason(this.live.queue(), this.live.voiceBatch())),
  );

  #showTimer: ReturnType<typeof setTimeout> | undefined;
  #hideTimer: ReturnType<typeof setTimeout> | undefined;
  /** The mark Escape gave focus back to: that focus does not reopen its card. */
  #closedOn: HTMLElement | null = null;

  protected override connected(): void {
    this.classList.add('pron-card');
    const root = this.parentElement;
    if (root) {
      root.addEventListener('pointerover', this.#onPointerOver);
      root.addEventListener('pointerout', this.#onPointerOut);
      root.addEventListener('focusin', this.#onFocusIn);
      root.addEventListener('focusout', this.#onFocusOut);
      root.addEventListener('keydown', this.#onMarkKeydown);
    }
    this.addEventListener('keydown', this.#onKeydown);
    this.onDisconnect(() => {
      root?.removeEventListener('pointerover', this.#onPointerOver);
      root?.removeEventListener('pointerout', this.#onPointerOut);
      root?.removeEventListener('focusin', this.#onFocusIn);
      root?.removeEventListener('focusout', this.#onFocusOut);
      root?.removeEventListener('keydown', this.#onMarkKeydown);
      this.hide();
      this.player.stop();
    });
  }

  /** Opens the card for a mark, anchored to it. */
  show(anchor: HTMLElement): void {
    this.#cancelTimers();
    const current = this.#open();
    if (current?.anchor === anchor) return;
    const { itemId, pronunciationId, start, sentenceStart } = anchor.dataset;
    if (!itemId || !pronunciationId) return;
    current?.anchor.style.removeProperty('anchor-name');
    anchor.style.setProperty('anchor-name', ANCHOR);
    this.#open.set({
      anchor,
      itemId,
      pronunciationId,
      start: Number(start),
      sentenceStart: Number(sentenceStart),
    });
    void this.rendered().then(() => {
      const panel = this.#panel();
      if (this.#open()?.anchor === anchor && panel && !panel.matches(':popover-open'))
        panel.showPopover?.();
    });
  }

  hide(): void {
    this.#cancelTimers();
    const current = this.#open();
    if (!current) return;
    current.anchor.style.removeProperty('anchor-name');
    this.#open.set(null);
    const panel = this.#panel();
    if (panel?.matches(':popover-open')) panel.hidePopover?.();
  }

  #panel(): HTMLElement | null {
    return this.querySelector<HTMLElement>('.pron-card__panel');
  }

  #cancelTimers(): void {
    clearTimeout(this.#showTimer);
    clearTimeout(this.#hideTimer);
  }

  #hideSoon(): void {
    clearTimeout(this.#showTimer);
    clearTimeout(this.#hideTimer);
    this.#hideTimer = setTimeout(() => this.hide(), HIDE_DELAY_MS);
  }

  /** Inside the card or on the open mark: the card stays. */
  #holds(node: EventTarget | null): boolean {
    return node instanceof Node && (this.contains(node) || this.#open()?.anchor === node);
  }

  readonly #onPointerOver = (e: PointerEvent): void => {
    const target = e.target instanceof Element ? e.target : null;
    if (target && this.contains(target)) {
      this.#cancelTimers();
      return;
    }
    const mark = target?.closest<HTMLElement>('.r2m-pron-mark');
    if (!mark) return;
    clearTimeout(this.#hideTimer);
    clearTimeout(this.#showTimer);
    if (this.#open()) this.show(mark);
    else this.#showTimer = setTimeout(() => this.show(mark), SHOW_DELAY_MS);
  };

  readonly #onPointerOut = (e: PointerEvent): void => {
    const from = e.target instanceof Element ? e.target : null;
    if (!from?.closest('.r2m-pron-mark') && !(from && this.contains(from))) return;
    if (this.#holds(e.relatedTarget)) return;
    if (this.#open()) this.#hideSoon();
    else clearTimeout(this.#showTimer);
  };

  readonly #onFocusIn = (e: FocusEvent): void => {
    if (this.#holds(e.target)) {
      this.#cancelTimers();
      return;
    }
    const mark =
      e.target instanceof Element ? e.target.closest<HTMLElement>('.r2m-pron-mark') : null;
    const closedOn = this.#closedOn;
    this.#closedOn = null;
    if (mark && mark !== closedOn) this.show(mark);
  };

  readonly #onFocusOut = (e: FocusEvent): void => {
    if (this.#open() && !this.#holds(e.relatedTarget)) this.#hideSoon();
  };

  /** Enter on the open mark moves into the card. */
  readonly #onMarkKeydown = (e: KeyboardEvent): void => {
    const open = this.#open();
    if (e.key !== 'Enter' || !open || e.target !== open.anchor) return;
    e.preventDefault();
    this.querySelector<HTMLElement>('.pron-card__panel button:not([disabled])')?.focus();
  };

  readonly #onKeydown = (e: KeyboardEvent): void => {
    if (e.key !== 'Escape') return;
    const anchor = this.#open()?.anchor;
    if (!anchor) return;
    e.preventDefault();
    e.stopPropagation();
    this.hide();
    this.#closedOn = anchor;
    anchor.focus();
  };

  #edit(open: OpenMark): void {
    this.hide();
    this.emit<PronunciationEditRequest>(
      'pronunciation-edit',
      {
        pronunciationId: open.pronunciationId,
        place: { itemId: open.itemId, offset: open.start },
      },
      { bubbles: true },
    );
  }

  protected template() {
    const open = this.#open();
    const entry = open ? this.#marks().entries[open.pronunciationId] : undefined;
    // One panel element throughout: replacing it would drop it from the top layer.
    return html`<div
      class="r2m-menu pron-card__panel"
      popover="manual"
      role="dialog"
      aria-label=${entry ? `Pronunciation of ${entry.writtenForm}` : 'Pronunciation'}
    >
      ${open && entry ? this.#body(open, entry) : nothing}
    </div>`;
  }

  #body(open: OpenMark, entry: PronunciationRef) {
    const key = `${open.itemId}:${open.sentenceStart}`;
    const readOnly = this.#readOnly();
    const view = playView(key, this.blocked(), this.player.playing());
    const request = {
      itemId: open.itemId,
      sentenceStart: open.sentenceStart,
      draft: null,
      asWritten: false,
      voice: 'item' as const,
    };
    return html`<p class="pron-card__forms" data-testid="pron-card-forms">
        <span class="pron-card__written">${entry.writtenForm}</span>
        ${icon('arrow_forward', 'pron-card__arrow')}
        <span class="pron-card__spoken">${entry.spokenForm}</span>
      </p>
      <div class="pron-card__actions">
        ${blockable(
          view.reason,
          html`<button
            type="button"
            class="r2m-button r2m-button--text"
            data-action="play-sentence"
            aria-pressed=${view.phase === 'idle' ? 'false' : 'true'}
            ?disabled=${view.disabled}
            @click=${() => this.player.toggle(key, [request])}
          >
            ${
              view.phase === 'generating'
                ? html`${spinner(16)} Generating…`
                : html`${icon(view.phase === 'playing' ? 'stop' : 'play_arrow')} This sentence`
            }
          </button>`,
        )}
        ${blockable(
          readOnly,
          html`<button
            type="button"
            class="r2m-icon-button"
            data-action="edit-pronunciation"
            aria-label=${`Edit pronunciation of ${entry.writtenForm}`}
            data-tooltip=${readOnly === null ? 'Edit pronunciation' : nothing}
            ?disabled=${readOnly !== null}
            @click=${() => this.#edit(open)}
          >
            ${icon('edit')}
          </button>`,
        )}
      </div>`;
  }
}
define('r2m-pronunciation-card', PronunciationCard);
