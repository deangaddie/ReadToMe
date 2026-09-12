import {
  AfterViewChecked,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AudioGenEvent } from '@app/live/hub-events';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { AudioPhaseState, foldAudioCards } from './audio-cards';

const PHASE_ICON: Record<AudioPhaseState, string> = {
  pending: 'radio_button_unchecked',
  active: 'progress_activity',
  ok: 'check_circle',
  warn: 'warning',
  error: 'error',
};

/**
 * Audio pipeline cards (design §7): one card per item attempt with the five phase steps,
 * autoscroll that pauses when the user scrolls up.
 */
@Component({
  selector: 'r2m-stream-audio',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-stream-audio' },
  template: `
    <div class="r2m-stream-audio__scroll" #scroll (scroll)="onScroll()">
      @if (cards().length === 0) {
        <p class="r2m-stream-audio__empty">No audio generation yet.</p>
      }
      @for (card of cards(); track card.key) {
        <article
          class="r2m-stream-audio__card"
          [class.r2m-stream-audio__card--failed]="card.failed"
        >
          <header class="r2m-stream-audio__head">
            <span class="r2m-stream-audio__character">{{
              card.character || 'Unknown speaker'
            }}</span>
            <span class="r2m-stream-audio__attempt">attempt {{ card.attempt }}</span>
            @if (card.failed) {
              <r2m-status-chip status="error" label="Failed" compact />
            } @else if (isDone(card.phases[4]!.state)) {
              <r2m-status-chip
                [status]="card.phases[4]!.state === 'ok' ? 'ok' : 'warn'"
                [label]="card.phases[4]!.state === 'ok' ? 'Verified' : 'Rescued'"
                compact
              />
            } @else {
              <r2m-status-chip status="busy" label="Running" compact />
            }
          </header>
          @if (card.text) {
            <p class="r2m-stream-audio__text">{{ card.text }}</p>
          }
          <ol class="r2m-stream-audio__phases">
            @for (phase of card.phases; track phase.id) {
              <li class="r2m-stream-audio__phase" [attr.data-state]="phase.state">
                @if (phase.state === 'active') {
                  <mat-progress-spinner mode="indeterminate" diameter="16" />
                } @else {
                  <mat-icon aria-hidden="true">{{ icon(phase.state) }}</mat-icon>
                }
                <span class="r2m-stream-audio__phase-label">{{ phase.label }}</span>
                @if (phase.lines.length) {
                  <ul class="r2m-stream-audio__lines">
                    @for (line of phase.lines; track $index) {
                      <li>{{ line }}</li>
                    }
                  </ul>
                }
              </li>
            }
          </ol>
          @if (card.reason) {
            <p class="r2m-stream-audio__reason">{{ card.reason }}</p>
          }
        </article>
      }
    </div>
    @if (paused()) {
      <button
        mat-stroked-button
        type="button"
        class="r2m-stream-audio__jump"
        (click)="jumpToLatest()"
      >
        <mat-icon>arrow_downward</mat-icon>Jump to latest
      </button>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: block;
      min-height: 0;
      height: 100%;
    }
    .r2m-stream-audio__scroll {
      height: 100%;
      overflow: auto;
      padding: var(--r2m-space-2);
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .r2m-stream-audio__empty {
      margin: var(--r2m-space-4);
      text-align: center;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-stream-audio__card {
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface);
      padding: var(--r2m-space-2) var(--r2m-space-3);
    }
    .r2m-stream-audio__card--failed {
      border-color: var(--r2m-status-error);
    }
    .r2m-stream-audio__head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .r2m-stream-audio__character {
      flex: 1;
      font-weight: 500;
    }
    .r2m-stream-audio__attempt {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
    }
    .r2m-stream-audio__text {
      margin: var(--r2m-space-1) 0;
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .r2m-stream-audio__phases {
      list-style: none;
      margin: var(--r2m-space-1) 0 0;
      padding: 0;
      display: grid;
      gap: 2px;
    }
    .r2m-stream-audio__phase {
      display: grid;
      grid-template-columns: 20px 1fr;
      align-items: center;
      column-gap: var(--r2m-space-2);
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .r2m-stream-audio__phase mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .r2m-stream-audio__phase[data-state='ok'] {
      color: var(--r2m-status-ok);
    }
    .r2m-stream-audio__phase[data-state='warn'] {
      color: var(--r2m-status-warn);
    }
    .r2m-stream-audio__phase[data-state='error'] {
      color: var(--r2m-status-error);
    }
    .r2m-stream-audio__phase[data-state='active'] {
      color: var(--r2m-status-busy);
    }
    .r2m-stream-audio__phase-label {
      color: var(--r2m-text);
    }
    .r2m-stream-audio__lines {
      grid-column: 2;
      margin: 0;
      padding: 0 0 0 var(--r2m-space-1);
      list-style: none;
      font-family: var(--r2m-font-mono);
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
    }
    .r2m-stream-audio__reason {
      margin: var(--r2m-space-1) 0 0;
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-error);
    }
    .r2m-stream-audio__jump {
      position: absolute;
      right: var(--r2m-space-4);
      bottom: var(--r2m-space-4);
      background: var(--r2m-surface);
      box-shadow: var(--r2m-shadow-2);
    }
  `,
})
export class StreamAudio implements AfterViewChecked {
  readonly events = input.required<AudioGenEvent[]>();
  readonly maxCards = input(50);

  protected readonly cards = computed(() => foldAudioCards(this.events(), this.maxCards()));
  readonly paused = signal(false);

  private readonly scroll = viewChild.required<ElementRef<HTMLElement>>('scroll');

  ngAfterViewChecked(): void {
    if (!this.paused()) this.scrollToBottom();
  }

  protected onScroll(): void {
    const el = this.scroll().nativeElement;
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 8;
    this.paused.set(!atBottom);
  }

  protected jumpToLatest(): void {
    this.paused.set(false);
    this.scrollToBottom();
  }

  protected icon(state: AudioPhaseState): string {
    return PHASE_ICON[state];
  }

  protected isDone(state: AudioPhaseState): boolean {
    return state === 'ok' || state === 'warn';
  }

  private scrollToBottom(): void {
    const el = this.scroll().nativeElement;
    el.scrollTop = el.scrollHeight;
  }
}
