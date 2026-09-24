import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import {
  SemanticSimilaritySettingsApi,
  SimilarityTestResponse,
  TranscriptionSettingsApi,
  VoiceDesignSettingsApi,
  toApiError,
} from '@app/api';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { FileDrop, RejectedFile } from '@app/ui/file-drop/file-drop';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ProviderConfig } from './provider-form';

/** The card every test panel sits in, and the run state they share. */
const PANEL_STYLES = `
  :host {
    display: flex;
    flex-direction: column;
    gap: var(--r2m-space-3);
    padding: var(--r2m-space-4);
    border: 1px solid var(--r2m-outline);
    border-radius: var(--r2m-radius-lg);
    background: var(--r2m-surface);
  }
  .test__title {
    margin: 0;
    font-size: var(--r2m-text-lg);
    font-weight: 600;
  }
  .test__note {
    margin: 0;
    color: var(--r2m-text-muted);
    font-size: var(--r2m-text-sm);
  }
  .test__row {
    display: flex;
    flex-wrap: wrap;
    gap: var(--r2m-space-3);
    align-items: center;
  }
  .test__grow {
    flex: 1 1 240px;
  }
  .test__result {
    margin: 0;
    white-space: pre-wrap;
  }
`;

/**
 * One Test action against a *saved* config. `run` reports a failure — a 422 carries the provider's
 * own reason — and drops the answer of a run whose config has since changed.
 */
@Directive()
abstract class TestPanel<TResult> {
  readonly config = input.required<ProviderConfig>();

  protected readonly running = signal(false);
  protected readonly result = signal<TResult | null>(null);
  protected readonly failure = signal<string | null>(null);

  private generation = 0;

  constructor() {
    // Another config is another test: whatever ran, or is still running, belongs to the last one.
    const configId = computed(() => this.config().id);
    effect(() => {
      configId();
      untracked(() => {
        this.generation++;
        this.running.set(false);
        this.result.set(null);
        this.failure.set(null);
      });
    });
  }

  protected async run(action: (configId: number) => Promise<TResult>): Promise<void> {
    const generation = this.generation;
    this.running.set(true);
    this.result.set(null);
    this.failure.set(null);
    try {
      const result = await action(this.config().id);
      if (generation === this.generation) this.result.set(result);
    } catch (e) {
      if (generation === this.generation) this.failure.set(`Test failed: ${toApiError(e).message}`);
    } finally {
      if (generation === this.generation) this.running.set(false);
    }
  }
}

/** Voice design: a prompt in, the designed voice speaking the sample text out. */
@Component({
  selector: 'app-voice-design-test',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    AudioPlayer,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="test__title">Test "{{ config().name }}"</h2>
    <p class="test__note">Designs a voice with the saved configuration; it can take up to a minute.</p>
    <div class="test__row">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="test__grow">
        <mat-label>Test prompt</mat-label>
        <input
          matInput
          autocomplete="off"
          data-field="prompt"
          placeholder="A warm, gravelly old man"
          [disabled]="running()"
          [value]="prompt()"
          (input)="prompt.set($any($event.target).value)"
        />
      </mat-form-field>
      <button
        mat-flat-button
        type="button"
        data-action="test"
        [disabled]="running() || !prompt().trim()"
        (click)="test()"
      >
        <mat-icon>graphic_eq</mat-icon> Test design
      </button>
    </div>
    @if (running()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (audioSrc(); as src) {
      <r2m-audio-player compact label="Designed voice" [src]="src" />
    }
    @if (failure(); as failure) {
      <r2m-status-chip status="error" [label]="failure" />
    }
  `,
  styles: PANEL_STYLES,
})
export class VoiceDesignTest extends TestPanel<string> {
  private readonly api = inject(VoiceDesignSettingsApi);

  protected readonly prompt = signal('');
  /** The answer as a data URI the player can take. */
  protected readonly audioSrc = this.result;

  protected test(): Promise<void> {
    const prompt = this.prompt().trim();
    return this.run(async (id) => {
      const audio = await this.api.test(id, prompt);
      return `data:${audio.contentType};base64,${audio.audioBase64}`;
    });
  }
}

const MAX_TEST_AUDIO_BYTES = 50 * 1024 * 1024;

/** Transcription: an audio file in, its transcript out. */
@Component({
  selector: 'app-transcription-test',
  imports: [MatProgressBarModule, FileDrop, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="test__title">Test "{{ config().name }}"</h2>
    <p class="test__note">Transcribes an audio file with the saved configuration.</p>
    <r2m-file-drop
      accept=".wav,.mp3,.aac"
      label="Drop an audio file here"
      hint="WAV, MP3 or AAC, up to 50 MB"
      [maxBytes]="maxBytes"
      (files)="test($event)"
      (rejected)="reject($event)"
    />
    @if (running()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (result() !== null) {
      <div role="status" data-role="transcript">
        <p class="test__note">Transcript</p>
        <p class="test__result">{{ result() || '(empty)' }}</p>
      </div>
    }
    @if (failure(); as failure) {
      <r2m-status-chip status="error" [label]="failure" />
    }
  `,
  styles: PANEL_STYLES,
})
export class TranscriptionTest extends TestPanel<string> {
  private readonly api = inject(TranscriptionSettingsApi);

  protected readonly maxBytes = MAX_TEST_AUDIO_BYTES;

  protected test(files: File[]): void {
    const [audio] = files;
    if (audio && !this.running()) void this.run((id) => this.api.test(id, audio));
  }

  protected reject(rejected: RejectedFile[]): void {
    this.result.set(null);
    this.failure.set(
      rejected[0]?.reason === 'size'
        ? 'Audio exceeds the 50 MB limit.'
        : 'Unsupported format. Use WAV, MP3 or AAC.',
    );
  }
}

/** Similarity: two texts in, the score against the config's pass threshold out. */
@Component({
  selector: 'app-similarity-test',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="test__title">Test "{{ config().name }}"</h2>
    <p class="test__note">Compares two texts with the saved configuration.</p>
    <div class="test__row">
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="test__grow">
        <mat-label>Text 1</mat-label>
        <input
          matInput
          autocomplete="off"
          data-field="text1"
          [value]="text1()"
          (input)="text1.set($any($event.target).value)"
        />
      </mat-form-field>
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="test__grow">
        <mat-label>Text 2</mat-label>
        <input
          matInput
          autocomplete="off"
          data-field="text2"
          [value]="text2()"
          (input)="text2.set($any($event.target).value)"
        />
      </mat-form-field>
      <button
        mat-flat-button
        type="button"
        data-action="test"
        [disabled]="!canCompare()"
        (click)="test()"
      >
        <mat-icon>compare_arrows</mat-icon> Compare
      </button>
    </div>
    @if (running()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (result(); as result) {
      <r2m-status-chip
        data-role="score"
        [status]="result.pass ? 'ok' : 'warn'"
        [label]="
          'Score ' +
          result.score.toFixed(3) +
          ' — ' +
          (result.pass ? 'PASS' : 'FAIL') +
          ' (threshold ' +
          result.threshold.toFixed(2) +
          ')'
        "
      />
    }
    @if (failure(); as failure) {
      <r2m-status-chip status="error" [label]="failure" />
    }
  `,
  styles: PANEL_STYLES,
})
export class SimilarityTest extends TestPanel<SimilarityTestResponse> {
  private readonly api = inject(SemanticSimilaritySettingsApi);

  protected readonly text1 = signal('');
  protected readonly text2 = signal('');
  protected readonly canCompare = computed(
    () => !this.running() && !!this.text1().trim() && !!this.text2().trim(),
  );

  protected test(): Promise<void> {
    const [text1, text2] = [this.text1().trim(), this.text2().trim()];
    return this.run((id) => this.api.test(id, text1, text2));
  }
}
