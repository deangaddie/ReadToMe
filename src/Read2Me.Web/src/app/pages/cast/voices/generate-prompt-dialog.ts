import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Guid, VoicesApi, toApiError } from '@app/api';
import { ActivityStore } from '@app/activity/activity-store';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { firstValueFrom } from 'rxjs';

export interface GeneratePromptDialogData {
  folder: string;
  characterId: Guid;
  characterName: string;
}

export interface GeneratePromptDialogResult {
  designPrompt: string;
}

export type GeneratePromptPhase = 'rendering' | 'edit' | 'generating';

/** The activity centre's id for the single voice-prompt run (one at a time, like Blazor's state). */
export const VOICE_PROMPT_JOB_ID = 'voicePrompt';

/**
 * Blazor's "Regenerate with AI" (research §4): the host renders the voice-design prompt template
 * for the character, the user reviews or edits it, Send to AI asks the LLM and the answer comes
 * back as the card's prompt draft. The LLM stream is inline while generating, and the run is
 * registered with the activity centre so the pill shows it. Cancel while generating closes the
 * dialog; the host finishes on its own and the answer is dropped.
 */
@Component({
  selector: 'app-generate-prompt-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatProgressBarModule,
    TextFieldModule,
    StreamLlm,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Generate voice prompt with AI</h2>
    <mat-dialog-content class="gen-prompt" [attr.data-phase]="phase()">
      @if (error(); as error) {
        <p class="gen-prompt__error" role="alert">{{ error }}</p>
      }
      @if (phase() === 'rendering') {
        <mat-progress-bar mode="indeterminate" aria-label="Rendering prompt" />
        <p class="gen-prompt__hint">Rendering the prompt for {{ data.characterName }}…</p>
      } @else {
        <p class="gen-prompt__hint">Review or edit the prompt before sending to the LLM.</p>
        <mat-form-field appearance="outline" class="gen-prompt__field" subscriptSizing="dynamic">
          <mat-label>Prompt</mat-label>
          <textarea
            matInput
            cdkTextareaAutosize
            cdkAutosizeMinRows="8"
            cdkAutosizeMaxRows="18"
            aria-label="Prompt"
            [value]="prompt()"
            [disabled]="phase() === 'generating'"
            (input)="prompt.set($any($event.target).value)"
          ></textarea>
        </mat-form-field>
        @if (phase() === 'generating') {
          <mat-progress-bar mode="indeterminate" aria-label="Generating" />
        }
      }

      <div class="gen-prompt__stream">
        <button mat-button type="button" (click)="showStream.set(!showStream())">
          <mat-icon>{{ showStream() ? 'keyboard_arrow_down' : 'keyboard_arrow_up' }}</mat-icon>
          {{ showStream() ? 'Hide AI activity' : 'Show AI activity' }}
        </button>
        <div class="gen-prompt__stream-body" [class.gen-prompt__stream-body--open]="showStream()">
          <r2m-stream-llm [events]="feed.events()" [maxTurns]="feed.maxUnits" />
        </div>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        data-action="send"
        [disabled]="!canSend()"
        (click)="generate()"
      >
        <mat-icon>auto_awesome</mat-icon> Send to AI
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .gen-prompt {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
      width: min(720px, 90vw);
    }
    .gen-prompt__field {
      width: 100%;
    }
    .gen-prompt__hint {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .gen-prompt__error {
      margin: 0;
      color: var(--r2m-status-error);
    }
    .gen-prompt__stream {
      border-top: 1px solid var(--r2m-outline);
      padding-top: var(--r2m-space-1);
    }
    .gen-prompt__stream-body {
      height: 0;
      overflow: hidden;
      transition: height 0.15s ease;
    }
    .gen-prompt__stream-body--open {
      height: 32vh;
    }
  `,
})
export class GeneratePromptDialog implements OnDestroy {
  protected readonly data = inject<GeneratePromptDialogData>(MAT_DIALOG_DATA);
  protected readonly ref =
    inject<MatDialogRef<GeneratePromptDialog, GeneratePromptDialogResult>>(MatDialogRef);
  protected readonly feed = inject(LlmStreamFeed);
  private readonly voices = inject(VoicesApi);
  private readonly activity = inject(ActivityStore);

  protected readonly phase = signal<GeneratePromptPhase>('rendering');
  protected readonly prompt = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly showStream = signal(false);
  protected readonly canSend = computed(
    () => this.phase() === 'edit' && this.prompt().trim().length > 0,
  );

  /** Which host call is current; an answer from an earlier one is dropped. */
  private run = 0;
  private unregisterJob: (() => void) | null = null;

  constructor() {
    this.feed.acquire();
    void this.render();
  }

  ngOnDestroy(): void {
    this.feed.release();
    this.run++;
    this.unregisterJob?.();
  }

  private async render(): Promise<void> {
    const run = ++this.run;
    try {
      const rendered = await this.voices.renderDesignPrompt(
        this.data.folder,
        this.data.characterId,
      );
      if (run !== this.run) return;
      this.prompt.set(rendered.prompt);
      this.phase.set('edit');
    } catch (e) {
      if (run !== this.run) return;
      this.error.set(toApiError(e).message);
      this.phase.set('edit');
    }
  }

  protected async generate(): Promise<void> {
    if (!this.canSend()) return;
    const run = ++this.run;
    this.error.set(null);
    this.phase.set('generating');
    this.unregisterJob = this.activity.registerLocalJob({
      id: VOICE_PROMPT_JOB_ID,
      kind: 'voicePrompt',
      label: `Voice prompt · ${this.data.characterName}`,
      state: 'running',
      detail: 'Asking the LLM',
    });
    try {
      const result = await this.voices.generateDesignPrompt(
        this.data.folder,
        this.data.characterId,
        this.prompt(),
      );
      if (run !== this.run) return;
      this.ref.close({ designPrompt: result.designPrompt });
    } catch (e) {
      if (run !== this.run) return;
      this.error.set(toApiError(e).message);
      this.phase.set('edit');
    } finally {
      this.unregisterJob?.();
      this.unregisterJob = null;
    }
  }
}

/** Opens the dialog; resolves the generated design prompt, or null when cancelled. */
export async function openGeneratePromptDialog(
  dialog: MatDialog,
  data: GeneratePromptDialogData,
): Promise<GeneratePromptDialogResult | null> {
  const ref = dialog.open<
    GeneratePromptDialog,
    GeneratePromptDialogData,
    GeneratePromptDialogResult
  >(GeneratePromptDialog, { data, autoFocus: 'dialog', restoreFocus: true });
  return (await firstValueFrom(ref.afterClosed())) ?? null;
}
