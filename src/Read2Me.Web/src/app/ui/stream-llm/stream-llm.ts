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
import { LlmStreamEvent } from '@app/live/hub-events';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';
import { LlmTurn, foldLlmTurns } from './llm-turns';

const STATE_VIEW: Record<LlmTurn['state'], { kind: StatusKind; label: string }> = {
  open: { kind: 'busy', label: 'Streaming' },
  completed: { kind: 'ok', label: 'Completed' },
  failed: { kind: 'error', label: 'Failed' },
  aborted: { kind: 'warn', label: 'Aborted' },
};

/**
 * LLM turn list (design §7): one expandable card per request with prompt, thinking and response,
 * escalation/run markers inline, autoscroll that pauses when the user scrolls up.
 */
@Component({
  selector: 'r2m-stream-llm',
  imports: [MatButtonModule, MatIconModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-stream-llm' },
  template: `
    <div class="r2m-stream-llm__scroll" #scroll (scroll)="onScroll()">
      @if (rows().length === 0) {
        <p class="r2m-stream-llm__empty">No LLM traffic yet.</p>
      }
      @for (row of rows(); track row.seq) {
        @if (row.kind === 'marker') {
          <div class="r2m-stream-llm__marker">
            <mat-icon aria-hidden="true">{{ row.icon }}</mat-icon
            ><span>{{ row.text }}</span>
          </div>
        } @else {
          <details class="r2m-stream-llm__turn" [attr.data-state]="row.state" open>
            <summary class="r2m-stream-llm__head">
              <span class="r2m-stream-llm__preview">{{ row.paragraphPreview }}</span>
              <span class="r2m-stream-llm__config">{{ row.configName }}</span>
              <r2m-status-chip
                [status]="stateView(row).kind"
                [label]="stateView(row).label"
                compact
              />
              @if (stats(row); as s) {
                <span class="r2m-stream-llm__stats">{{ s }}</span>
              }
            </summary>
            <div class="r2m-stream-llm__body">
              <details class="r2m-stream-llm__section">
                <summary>Prompt</summary>
                <pre class="r2m-stream-llm__pre">{{ row.prompt }}</pre>
              </details>
              @if (row.thinking) {
                <details class="r2m-stream-llm__section r2m-stream-llm__section--thinking">
                  <summary>Thinking</summary>
                  <pre class="r2m-stream-llm__pre">{{ row.thinking }}</pre>
                </details>
              }
              <pre class="r2m-stream-llm__pre r2m-stream-llm__response">{{ row.response }}</pre>
              @if (row.reason) {
                <p class="r2m-stream-llm__reason">{{ row.reason }}</p>
              }
            </div>
          </details>
        }
      }
    </div>
    @if (paused()) {
      <button
        mat-stroked-button
        type="button"
        class="r2m-stream-llm__jump"
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
    .r2m-stream-llm__scroll {
      height: 100%;
      overflow: auto;
      padding: var(--r2m-space-2);
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .r2m-stream-llm__empty {
      margin: var(--r2m-space-4);
      text-align: center;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-stream-llm__marker {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-info);
    }
    .r2m-stream-llm__marker mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .r2m-stream-llm__turn {
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface);
    }
    .r2m-stream-llm__head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      cursor: pointer;
      list-style: none;
    }
    .r2m-stream-llm__preview {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-weight: 500;
    }
    .r2m-stream-llm__config,
    .r2m-stream-llm__stats {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      white-space: nowrap;
    }
    .r2m-stream-llm__body {
      padding: 0 var(--r2m-space-3) var(--r2m-space-2);
    }
    .r2m-stream-llm__section summary {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      cursor: pointer;
    }
    .r2m-stream-llm__section--thinking .r2m-stream-llm__pre {
      color: var(--r2m-text-muted);
    }
    .r2m-stream-llm__pre {
      margin: var(--r2m-space-1) 0;
      font-family: var(--r2m-font-mono);
      font-size: var(--r2m-text-sm);
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 240px;
      overflow: auto;
    }
    .r2m-stream-llm__reason {
      margin: 0;
      font-size: var(--r2m-text-sm);
      color: var(--r2m-status-error);
    }
    .r2m-stream-llm__jump {
      position: absolute;
      right: var(--r2m-space-4);
      bottom: var(--r2m-space-4);
      background: var(--r2m-surface);
      box-shadow: var(--r2m-shadow-2);
    }
  `,
})
export class StreamLlm implements AfterViewChecked {
  readonly events = input.required<LlmStreamEvent[]>();
  readonly maxTurns = input(50);

  protected readonly rows = computed(() => foldLlmTurns(this.events(), this.maxTurns()));
  /** True once the user scrolled up; autoscroll resumes after Jump or reaching the bottom. */
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

  protected stateView(turn: LlmTurn) {
    return STATE_VIEW[turn.state];
  }

  protected stats(turn: LlmTurn): string | null {
    const parts: string[] = [];
    if (turn.tokensOut != null) parts.push(`${turn.tokensOut} tok`);
    if (turn.tokensPerSecond != null) parts.push(`${turn.tokensPerSecond.toFixed(1)} tok/s`);
    if (turn.generationMs != null) parts.push(`${(turn.generationMs / 1000).toFixed(1)} s`);
    return parts.length ? parts.join(' · ') : null;
  }

  private scrollToBottom(): void {
    const el = this.scroll().nativeElement;
    el.scrollTop = el.scrollHeight;
  }
}
