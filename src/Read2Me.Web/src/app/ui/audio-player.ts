import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { computed, signal } from '@app/core/signals';
import type { IconName } from './icons';
import { icon } from './partials';

export type AbSide = 'a' | 'b';

function withCacheKey(src: string | null, key: string | number | undefined): string | null {
  if (!src) return null;
  if (key === undefined || key === '') return src;
  return `${src}${src.includes('?') ? '&' : '?'}v=${encodeURIComponent(String(key))}`;
}

export function formatClock(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) return '–:––';
  const total = Math.max(0, Math.floor(seconds));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}

/**
 * Audio player (design §7): `<audio>` wrapper with play/pause, scrub and duration. `cacheKey`
 * busts the browser cache for files regenerated in place. Set `srcB` for the A/B variant: a
 * two-segment toggle swaps sources while keeping position and play state. Emits
 * `playback-ended` and `ab` (the side picked).
 */
export class AudioPlayer extends R2mElement {
  #src = signal<string | null>(null);
  get src() {
    return this.#src();
  }
  set src(value: string | null) {
    this.#src.set(value);
  }

  #cacheKey = signal<string | number | undefined>(undefined);
  get cacheKey() {
    return this.#cacheKey();
  }
  set cacheKey(value: string | number | undefined) {
    this.#cacheKey.set(value);
  }

  #label = signal<string | undefined>(undefined);
  get label() {
    return this.#label();
  }
  set label(value: string | undefined) {
    this.#label.set(value);
  }

  #compact = signal(false);
  get compact() {
    return this.#compact();
  }
  set compact(value: boolean) {
    this.#compact.set(value);
  }

  #srcB = signal<string | null>(null);
  get srcB() {
    return this.#srcB();
  }
  set srcB(value: string | null) {
    this.#srcB.set(value);
  }

  #labelA = signal('A');
  get labelA() {
    return this.#labelA();
  }
  set labelA(value: string) {
    this.#labelA.set(value);
  }

  #labelB = signal('B');
  get labelB() {
    return this.#labelB();
  }
  set labelB(value: string) {
    this.#labelB.set(value);
  }

  readonly playing = signal(false);
  readonly currentTime = signal(0);
  readonly duration = signal<number | null>(null);
  readonly error = signal(false);
  readonly side = signal<AbSide>('a');

  /** Position/play state to restore after an A/B source swap. */
  #restore: { time: number; play: boolean } | null = null;

  readonly resolvedSrc = computed(() => {
    const raw = this.side() === 'b' && this.#srcB() ? this.#srcB() : this.#src();
    return withCacheKey(raw, this.#cacheKey());
  });

  readonly #clock = computed(
    () => `${formatClock(this.currentTime())} / ${formatClock(this.duration())}`,
  );

  protected override connected(): void {
    this.classList.add('r2m-audio-player');
    this.effect(() => {
      this.classList.toggle('r2m-audio-player--compact', this.#compact());
      this.classList.toggle('r2m-audio-player--error', this.error());
    });
  }

  get #audio(): HTMLAudioElement | null {
    return this.querySelector('audio');
  }

  toggle(): void {
    const audio = this.#audio;
    if (!audio) return;
    if (this.playing()) audio.pause();
    else void audio.play().catch(() => this.error.set(true));
  }

  seek(seconds: number): void {
    if (!Number.isFinite(seconds)) return;
    const audio = this.#audio;
    if (audio) audio.currentTime = seconds;
    this.currentTime.set(seconds);
  }

  switchSide(side: AbSide): void {
    if (side === this.side()) return;
    const audio = this.#audio;
    this.#restore = { time: audio?.currentTime ?? 0, play: this.playing() };
    this.error.set(false);
    this.duration.set(null);
    this.side.set(side);
    this.emit('ab', side);
  }

  protected template() {
    const src = this.resolvedSrc();
    const error = this.error();
    const playing = this.playing();
    const duration = this.duration();
    const label = this.#label();
    const glyph: IconName = error ? 'warning' : playing ? 'pause' : 'play_arrow';
    return html`
      <audio
        preload="metadata"
        src=${src ?? nothing}
        @loadedmetadata=${this.#onLoadedMetadata}
        @timeupdate=${this.#onTimeUpdate}
        @play=${() => this.playing.set(true)}
        @pause=${() => this.playing.set(false)}
        @ended=${this.#onEnded}
        @error=${this.#onError}
      ></audio>

      <button
        type="button"
        class="r2m-audio-player__toggle"
        ?disabled=${!src || error}
        aria-label=${playing ? 'Pause' : 'Play'}
        @click=${() => this.toggle()}
      >
        ${icon(glyph)}
      </button>

      ${label ? html`<span class="r2m-audio-player__label">${label}</span>` : nothing}
      ${
        error
          ? html`<span class="r2m-audio-player__error">Can't play</span>`
          : html`<input
                type="range"
                class="r2m-audio-player__scrub"
                min="0"
                max=${duration ?? 0}
                step="0.01"
                .value=${String(this.currentTime())}
                ?disabled=${!duration}
                aria-label="Seek"
                @input=${(e: Event) => this.seek((e.target as HTMLInputElement).valueAsNumber)}
              />
              <span class="r2m-audio-player__time">${this.#clock()}</span>`
      }
      ${this.#srcB() ? this.#abToggle() : nothing}
    `;
  }

  #abToggle() {
    const side = this.side();
    const button = (which: AbSide, text: string) =>
      html`<button
        type="button"
        class="r2m-audio-player__ab-btn ${side === which ? 'r2m-audio-player__ab-btn--active' : ''}"
        aria-pressed=${side === which ? 'true' : 'false'}
        @click=${() => this.switchSide(which)}
      >
        ${text}
      </button>`;
    return html`<span class="r2m-audio-player__ab" role="group" aria-label="Compare">
      ${button('a', this.#labelA())} ${button('b', this.#labelB())}
    </span>`;
  }

  readonly #onLoadedMetadata = (): void => {
    const audio = this.#audio;
    if (!audio) return;
    this.error.set(false);
    this.duration.set(Number.isFinite(audio.duration) ? audio.duration : null);
    const restore = this.#restore;
    this.#restore = null;
    if (restore) {
      this.seek(Math.min(restore.time, audio.duration || restore.time));
      if (restore.play) void audio.play().catch(() => this.error.set(true));
    }
  };

  readonly #onTimeUpdate = (): void => {
    const audio = this.#audio;
    if (audio) this.currentTime.set(audio.currentTime);
  };

  readonly #onEnded = (): void => {
    this.playing.set(false);
    this.emit('playback-ended');
  };

  readonly #onError = (): void => {
    if (!this.resolvedSrc()) return;
    this.error.set(true);
    this.playing.set(false);
  };
}
define('r2m-audio-player', AudioPlayer);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-audio-player': AudioPlayer;
  }
}
