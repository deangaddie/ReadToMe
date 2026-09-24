import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

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
 * two-segment toggle swaps sources while keeping position and play state.
 */
@Component({
  selector: 'r2m-audio-player',
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-audio-player',
    '[class.r2m-audio-player--compact]': 'compact()',
    '[class.r2m-audio-player--error]': 'error()',
  },
  template: `
    <audio
      #audio
      preload="metadata"
      [src]="resolvedSrc()"
      (loadedmetadata)="onLoadedMetadata()"
      (timeupdate)="onTimeUpdate()"
      (play)="playing.set(true)"
      (pause)="playing.set(false)"
      (ended)="onEnded()"
      (error)="onError()"
    ></audio>

    <button
      type="button"
      class="r2m-audio-player__toggle"
      [disabled]="!resolvedSrc() || error()"
      [attr.aria-label]="playing() ? 'Pause' : 'Play'"
      (click)="toggle()"
    >
      <mat-icon aria-hidden="true">{{
        error() ? 'warning' : playing() ? 'pause' : 'play_arrow'
      }}</mat-icon>
    </button>

    @if (label()) {
      <span class="r2m-audio-player__label">{{ label() }}</span>
    }

    @if (error()) {
      <span class="r2m-audio-player__error">Can't play</span>
    } @else {
      <input
        type="range"
        class="r2m-audio-player__scrub"
        min="0"
        [max]="duration() ?? 0"
        step="0.01"
        [value]="currentTime()"
        [disabled]="!duration()"
        aria-label="Seek"
        (input)="seek($any($event.target).valueAsNumber)"
      />
      <span class="r2m-audio-player__time">{{ clock() }}</span>
    }

    @if (srcB()) {
      <span class="r2m-audio-player__ab" role="group" aria-label="Compare">
        <button
          type="button"
          class="r2m-audio-player__ab-btn"
          [class.r2m-audio-player__ab-btn--active]="side() === 'a'"
          [attr.aria-pressed]="side() === 'a'"
          (click)="switchSide('a')"
        >
          {{ labelA() }}
        </button>
        <button
          type="button"
          class="r2m-audio-player__ab-btn"
          [class.r2m-audio-player__ab-btn--active]="side() === 'b'"
          [attr.aria-pressed]="side() === 'b'"
          (click)="switchSide('b')"
        >
          {{ labelB() }}
        </button>
      </span>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-2);
      min-width: 220px;
      height: 32px;
      padding: 0 var(--r2m-space-2) 0 var(--r2m-space-1);
      border-radius: var(--r2m-radius-pill);
      background: var(--r2m-surface-low);
      color: var(--r2m-text);
      font-size: var(--r2m-text-sm);
    }
    :host(.r2m-audio-player--compact) {
      min-width: 160px;
      height: 28px;
      gap: var(--r2m-space-1);
    }
    audio {
      display: none;
    }
    .r2m-audio-player__toggle {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 28px;
      height: 28px;
      border: none;
      border-radius: 50%;
      background: transparent;
      color: var(--r2m-accent);
      cursor: pointer;
      padding: 0;
    }
    .r2m-audio-player__toggle:disabled {
      color: var(--r2m-text-muted);
      cursor: default;
    }
    .r2m-audio-player__toggle:focus-visible {
      outline: 2px solid var(--r2m-accent);
    }
    :host(.r2m-audio-player--error) .r2m-audio-player__toggle {
      color: var(--r2m-status-warn);
    }
    .r2m-audio-player__label {
      color: var(--r2m-text-muted);
      white-space: nowrap;
    }
    .r2m-audio-player__scrub {
      flex: 1 1 auto;
      min-width: 60px;
      accent-color: var(--r2m-accent);
      margin: 0;
    }
    .r2m-audio-player__time {
      font-variant-numeric: tabular-nums;
      color: var(--r2m-text-muted);
      white-space: nowrap;
    }
    .r2m-audio-player__error {
      flex: 1 1 auto;
      color: var(--r2m-status-warn);
    }
    .r2m-audio-player__ab {
      display: inline-flex;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-pill);
      overflow: hidden;
    }
    .r2m-audio-player__ab-btn {
      border: none;
      background: transparent;
      color: var(--r2m-text-muted);
      font: inherit;
      font-size: var(--r2m-text-xs);
      font-weight: 600;
      padding: 2px var(--r2m-space-2);
      cursor: pointer;
    }
    .r2m-audio-player__ab-btn--active {
      background: var(--r2m-accent);
      color: var(--mat-sys-on-primary);
    }
  `,
})
export class AudioPlayer {
  readonly src = input<string | null>(null);
  readonly cacheKey = input<string | number>();
  readonly label = input<string>();
  readonly compact = input(false, { transform: booleanAttribute });
  readonly srcB = input<string | null>(null);
  readonly labelA = input('A');
  readonly labelB = input('B');

  readonly playbackEnded = output<void>();
  readonly ab = output<AbSide>();

  private readonly audioRef = viewChild.required<ElementRef<HTMLAudioElement>>('audio');

  readonly playing = signal(false);
  readonly currentTime = signal(0);
  readonly duration = signal<number | null>(null);
  readonly error = signal(false);
  readonly side = signal<AbSide>('a');

  /** Position/play state to restore after an A/B source swap. */
  private restore: { time: number; play: boolean } | null = null;

  readonly resolvedSrc = computed(() => {
    const raw = this.side() === 'b' && this.srcB() ? this.srcB() : this.src();
    return withCacheKey(raw, this.cacheKey());
  });

  protected readonly clock = computed(
    () => `${formatClock(this.currentTime())} / ${formatClock(this.duration())}`,
  );

  private get audio(): HTMLAudioElement {
    return this.audioRef().nativeElement;
  }

  toggle(): void {
    if (this.playing()) this.audio.pause();
    else void this.audio.play().catch(() => this.error.set(true));
  }

  seek(seconds: number): void {
    if (!Number.isFinite(seconds)) return;
    this.audio.currentTime = seconds;
    this.currentTime.set(seconds);
  }

  switchSide(side: AbSide): void {
    if (side === this.side()) return;
    this.restore = { time: this.audio.currentTime, play: this.playing() };
    this.error.set(false);
    this.duration.set(null);
    this.side.set(side);
    this.ab.emit(side);
  }

  protected onLoadedMetadata(): void {
    this.error.set(false);
    this.duration.set(Number.isFinite(this.audio.duration) ? this.audio.duration : null);
    const restore = this.restore;
    this.restore = null;
    if (restore) {
      this.seek(Math.min(restore.time, this.audio.duration || restore.time));
      if (restore.play) void this.audio.play().catch(() => this.error.set(true));
    }
  }

  protected onTimeUpdate(): void {
    this.currentTime.set(this.audio.currentTime);
  }

  protected onEnded(): void {
    this.playing.set(false);
    this.playbackEnded.emit();
  }

  protected onError(): void {
    if (!this.resolvedSrc()) return;
    this.error.set(true);
    this.playing.set(false);
  }
}
