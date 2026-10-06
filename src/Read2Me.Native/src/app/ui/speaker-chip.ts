import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import { speakerHue } from '@app/shared/speaker-color';
import { icon } from './partials';
import type { SpeakerMenu, SpeakerRosterEntry } from './speaker-menu';
import './speaker-menu';

export type SpeakerChipState = 'named' | 'unknown' | 'mixed' | 'narration' | 'narrator-linked';

let nextId = 0;

/**
 * Speaker chip (design §7): a speaker name with a stable per-character hue. Given a `roster` it is
 * a button that opens `r2m-speaker-menu` in a popover — the browser handles light dismiss, Escape
 * and the top layer; CSS anchor positioning places it under the chip (the button is the popover's
 * implicit anchor). The menu is rendered only while open, so every open starts with an empty
 * search. The menu's `pick` / `clear` / `create` events bubble out through the chip.
 */
export class SpeakerChip extends R2mElement {
  #name = signal('');
  get name() {
    return this.#name();
  }
  set name(value: string) {
    this.#name.set(value);
  }

  #characterId = signal<string | undefined>(undefined);
  get characterId() {
    return this.#characterId();
  }
  set characterId(value: string | undefined) {
    this.#characterId.set(value);
  }

  #state = signal<SpeakerChipState>('named');
  get state() {
    return this.#state();
  }
  set state(value: SpeakerChipState) {
    this.#state.set(value);
  }

  #roster = signal<readonly SpeakerRosterEntry[] | undefined>(undefined);
  get roster() {
    return this.#roster();
  }
  set roster(value: readonly SpeakerRosterEntry[] | undefined) {
    this.#roster.set(value);
  }

  #compact = signal(false);
  get compact() {
    return this.#compact();
  }
  set compact(value: boolean) {
    this.#compact.set(value);
  }

  readonly #menuId = `r2m-speaker-chip-menu-${nextId++}`;
  readonly #open = signal(false);
  readonly #label = computed(() => {
    const name = this.#name();
    switch (this.#state()) {
      case 'unknown':
        return name || 'Unknown';
      case 'mixed':
        return name || 'Mixed';
      case 'narration':
        return name || 'Narration';
      default:
        return name;
    }
  });

  protected override connected(): void {
    this.classList.add('r2m-speaker-chip');
    this.effect(() => {
      for (const s of ['named', 'unknown', 'mixed', 'narration', 'narrator-linked'])
        this.classList.toggle(`r2m-speaker-chip--${s}`, s === this.#state());
      this.classList.toggle('r2m-speaker-chip--compact', this.#compact());
      this.classList.toggle('r2m-speaker-chip--interactive', !!this.#roster());
      this.style.setProperty(
        '--r2m-speaker-hue',
        String(speakerHue(this.#characterId() ?? this.#name() ?? '?')),
      );
    });
    // A pick, clear or create from the menu closes it, like a mat-menu item click.
    for (const type of ['pick', 'clear', 'create']) {
      const close = () => this.querySelector<HTMLElement>(`#${this.#menuId}`)?.hidePopover();
      this.addEventListener(type, close);
      this.onDisconnect(() => this.removeEventListener(type, close));
    }
  }

  protected template() {
    const roster = this.#roster();
    const content = this.#content();
    if (!roster) return html`<span class="r2m-speaker-chip__body">${content}</span>`;
    return html`
      <button
        type="button"
        class="r2m-speaker-chip__body"
        popovertarget=${this.#menuId}
        aria-haspopup="listbox"
      >
        ${content}
      </button>
      <div
        id=${this.#menuId}
        popover
        class="r2m-menu r2m-speaker-chip__menu"
        @toggle=${this.#onToggle}
      >
        ${
          this.#open()
            ? html`<r2m-speaker-menu
                .roster=${roster}
                .selectedId=${this.#characterId()}
              ></r2m-speaker-menu>`
            : nothing
        }
      </div>
    `;
  }

  #onToggle = (e: ToggleEvent): void => {
    this.#open.set(e.newState === 'open');
    // Mouse or trackpad: type to search straight away. Touch skips it (the keyboard would cover the list).
    if (e.newState === 'open' && matchMedia('(pointer: fine)').matches) {
      void this.rendered().then(() =>
        this.querySelector<SpeakerMenu>('r2m-speaker-menu')?.focusSearch(),
      );
    }
  };

  #content() {
    const state = this.#state();
    const glyph =
      state === 'unknown'
        ? 'question_mark'
        : state === 'mixed'
          ? 'call_split'
          : state === 'narration'
            ? 'auto_stories'
            : null;
    return html`${glyph ? icon(glyph, 'r2m-speaker-chip__icon') : html`<span class="r2m-speaker-chip__dot" aria-hidden="true"></span>`}
      <span class="r2m-speaker-chip__name">${this.#label()}</span>
      ${
        state === 'narrator-linked'
          ? html`<span
              class="material-symbols-rounded r2m-speaker-chip__link"
              aria-label="Linked narrator"
              >link</span
            >`
          : nothing
      }`;
  }
}
define('r2m-speaker-chip', SpeakerChip);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-speaker-chip': SpeakerChip;
  }
}
