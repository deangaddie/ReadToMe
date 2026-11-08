import { nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';

export type SplitterOrientation = 'vertical' | 'horizontal';

/** An arrow key's step, in px; Shift+arrow takes the large one. */
const STEP = 16;
const LARGE_STEP = 64;

/**
 * A resize gutter between two panes: a focusable `separator` whose value is the size, in px, of
 * the pane before it. It knows nothing about what it resizes; the consumer sets `value`, `min`,
 * `max`, `defaultValue`, `orientation` (ARIA's: `vertical` sits between columns) and `label`
 * (the accessible name), and listens for `value-change`.
 */
export class Splitter extends R2mElement {
  readonly #value = signal(0);
  get value() {
    return this.#value();
  }
  set value(value: number) {
    this.#value.set(value);
  }

  readonly #min = signal(0);
  get min() {
    return this.#min();
  }
  set min(value: number) {
    this.#min.set(value);
  }

  readonly #max = signal(0);
  get max() {
    return this.#max();
  }
  set max(value: number) {
    this.#max.set(value);
  }

  readonly #defaultValue = signal(0);
  get defaultValue() {
    return this.#defaultValue();
  }
  set defaultValue(value: number) {
    this.#defaultValue.set(value);
  }

  readonly #orientation = signal<SplitterOrientation>('vertical');
  get orientation() {
    return this.#orientation();
  }
  set orientation(value: SplitterOrientation) {
    this.#orientation.set(value);
  }

  readonly #label = signal('');
  get label() {
    return this.#label();
  }
  set label(value: string) {
    this.#label.set(value);
  }

  /** The pointer being dragged, where it went down, and the value then. */
  readonly #drag = signal<{ pointerId: number; origin: number; start: number } | null>(null);

  /** The upper bound, never below the lower one. */
  readonly #upper = computed(() => Math.max(this.#max(), this.#min()));

  /** The value shown and announced: always inside [min, max]. */
  readonly #clamped = computed(() => this.#clamp(this.#value()));

  protected override connected(): void {
    this.classList.add('r2m-splitter');
    this.setAttribute('role', 'separator');
    this.tabIndex = 0;
    this.effect(() => {
      const label = this.#label();
      if (label) this.setAttribute('aria-label', label);
      else this.removeAttribute('aria-label');
      this.setAttribute('aria-orientation', this.#orientation());
      this.setAttribute('aria-valuenow', String(this.#clamped()));
      this.setAttribute('aria-valuemin', String(this.#min()));
      this.setAttribute('aria-valuemax', String(this.#upper()));
    });
    this.effect(() =>
      this.classList.toggle('r2m-splitter--horizontal', this.#orientation() === 'horizontal'),
    );
    this.effect(() => this.classList.toggle('r2m-splitter--dragging', this.#drag() !== null));
    const listeners: [string, EventListener][] = [
      ['keydown', this.#onKeydown as EventListener],
      ['dblclick', this.#onDblclick],
      ['pointerdown', this.#onPointerdown as EventListener],
      ['pointermove', this.#onPointermove as EventListener],
      ['pointerup', this.#onPointerEnd as EventListener],
      ['pointercancel', this.#onPointerEnd as EventListener],
      ['lostpointercapture', this.#onPointerEnd as EventListener],
    ];
    for (const [type, listener] of listeners) this.addEventListener(type, listener);
    this.onDisconnect(() => {
      for (const [type, listener] of listeners) this.removeEventListener(type, listener);
      this.#drag.set(null);
    });
  }

  protected template() {
    return nothing;
  }

  readonly #onKeydown = (e: KeyboardEvent): void => {
    // Alt+arrow is the browser's Back/Forward, Ctrl/Cmd+Home/End its own jumps.
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    const step = e.shiftKey ? LARGE_STEP : STEP;
    // Only the arrows along the drag axis resize (WAI-ARIA window splitter); the others still scroll.
    const [less, more] =
      this.#orientation() === 'vertical' ? ['ArrowLeft', 'ArrowRight'] : ['ArrowUp', 'ArrowDown'];
    let next: number;
    if (e.key === more) next = this.#clamped() + step;
    else if (e.key === less) next = this.#clamped() - step;
    else if (e.key === 'Home') next = this.#min();
    else if (e.key === 'End') next = this.#upper();
    else return;
    e.preventDefault();
    this.#commit(next);
  };

  readonly #onDblclick = (): void => this.#commit(this.#defaultValue());

  readonly #onPointerdown = (e: PointerEvent): void => {
    if (e.button !== 0) return;
    // No text selection while dragging; the pointer stays ours outside the gutter. Preventing
    // the press may also skip its focus, so focus here: the arrow keys then work straight after.
    e.preventDefault();
    this.focus();
    this.setPointerCapture(e.pointerId);
    this.#drag.set({ pointerId: e.pointerId, origin: this.#position(e), start: this.#clamped() });
  };

  readonly #onPointermove = (e: PointerEvent): void => {
    const drag = this.#drag();
    if (drag?.pointerId !== e.pointerId) return;
    this.#commit(drag.start + this.#position(e) - drag.origin);
  };

  readonly #onPointerEnd = (e: PointerEvent): void => {
    if (this.#drag()?.pointerId !== e.pointerId) return;
    this.#drag.set(null);
    if (this.hasPointerCapture(e.pointerId)) this.releasePointerCapture(e.pointerId);
  };

  #position(e: PointerEvent): number {
    return this.#orientation() === 'vertical' ? e.clientX : e.clientY;
  }

  /** Sets the clamped value, in whole px, and emits `value-change` only when it actually moved. */
  #commit(next: number): void {
    const value = this.#clamp(Math.round(next));
    if (value === this.#clamped()) return;
    this.#value.set(value);
    this.emit('value-change', value);
  }

  #clamp(value: number): number {
    return Math.min(Math.max(value, this.#min()), this.#upper());
  }
}
define('r2m-splitter', Splitter);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-splitter': Splitter;
  }
}
