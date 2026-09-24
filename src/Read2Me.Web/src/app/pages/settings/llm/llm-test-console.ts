import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { LlmServerConfig, LlmSettingsApi, toApiError } from '@app/api';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { LiveService } from '@app/live/live.service';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { Throughput } from '@app/ui/throughput/throughput';

/**
 * Test console for the default config (ticket 21; Blazor's test panel): prompt, Send / Stop, the
 * LLM stream inline and the run's throughput. The host runs the send in the background; tokens
 * arrive on `stream:llm` and the ending as one `llmTest` message to this connection. A reconnect
 * may have swallowed that message, so the run state is read back on every resync.
 */
@Component({
  selector: 'app-llm-test-console',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    TextFieldModule,
    StreamLlm,
    Throughput,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="console__title">Test "{{ config().name }}"</h2>
    <mat-form-field appearance="outline" class="console__prompt" subscriptSizing="dynamic">
      <mat-label>Prompt</mat-label>
      <textarea
        matInput
        cdkTextareaAutosize
        cdkAutosizeMinRows="3"
        cdkAutosizeMaxRows="10"
        data-field="prompt"
        [disabled]="streaming()"
        [value]="prompt()"
        (input)="prompt.set($any($event.target).value)"
      ></textarea>
    </mat-form-field>
    <div class="console__actions">
      <button
        mat-flat-button
        type="button"
        data-action="send"
        [disabled]="!canSend()"
        (click)="send()"
      >
        <mat-icon>send</mat-icon> Send
      </button>
      @if (streaming()) {
        <button mat-stroked-button type="button" data-action="stop" (click)="stop()">
          <mat-icon>stop</mat-icon> Stop
        </button>
      }
    </div>
    @if (error()) {
      <p class="console__error" role="alert">{{ error() }}</p>
    }

    @if (live.throughput(); as t) {
      @if (t.hasRun) {
        <r2m-throughput class="console__throughput" [snapshot]="t" />
      }
    }

    <div class="console__stream">
      <r2m-stream-llm [events]="feed.events()" [maxTurns]="feed.maxUnits" />
    </div>
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .console__title {
      margin: 0 0 var(--r2m-space-3);
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .console__prompt {
      width: 100%;
    }
    .console__actions {
      display: flex;
      gap: var(--r2m-space-2);
      margin: var(--r2m-space-3) 0;
    }
    .console__error {
      margin: 0 0 var(--r2m-space-3);
      color: var(--r2m-status-error);
    }
    .console__throughput {
      margin-bottom: var(--r2m-space-3);
    }
    .console__stream {
      height: 40vh;
      border-top: 1px solid var(--r2m-outline);
    }
  `,
})
export class LlmTestConsole implements OnDestroy {
  private readonly api = inject(LlmSettingsApi);
  protected readonly live = inject(LiveService);
  protected readonly feed = inject(LlmStreamFeed);

  /** The default config — the one a test send goes to. */
  readonly config = input.required<LlmServerConfig>();

  protected readonly prompt = signal('');
  protected readonly streaming = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canSend = computed(() => !this.streaming() && !!this.prompt().trim());

  /** Bumped by every send and read-back, so a read-back that started earlier cannot undo them. */
  private stateSeq = 0;

  constructor() {
    this.feed.acquire();
    const destroyRef = inject(DestroyRef);
    this.live
      .on('llmTest')
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe((m) => {
        this.stateSeq++;
        this.streaming.set(false);
        if (m.kind === 'failed') this.error.set(`Request failed: ${m.reason ?? 'unknown error'}`);
      });
    // A send this connection did not start (another tab) ends without an llmTest message here.
    this.live
      .on('llm')
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe((m) => {
        if (m.kind === 'runEnded' && this.streaming()) void this.readBack();
      });
    this.live.resynced$.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => void this.readBack());
    void this.readBack();
  }

  ngOnDestroy(): void {
    this.feed.release();
  }

  protected async send(): Promise<void> {
    if (!this.canSend()) return;
    this.error.set(null);
    this.stateSeq++;
    this.streaming.set(true);
    try {
      await this.api.startTest(this.config().id, {
        prompt: this.prompt(),
        connectionId: this.live.connectionId(),
      });
    } catch (e) {
      const problem = toApiError(e);
      // 409: a send is already running (another tab, or one this page lost track of) — follow it.
      if (problem.status === 409) return;
      this.streaming.set(false);
      this.error.set(problem.message);
    }
  }

  protected async stop(): Promise<void> {
    try {
      await this.api.cancelTest(this.config().id);
    } catch (e) {
      this.error.set(toApiError(e).message);
    }
  }

  private async readBack(): Promise<void> {
    const seq = ++this.stateSeq;
    try {
      const { running } = await this.api.testStatus();
      if (seq === this.stateSeq) this.streaming.set(running);
    } catch {
      // The next resync or llmTest message settles it.
    }
  }
}
