import {
  ChangeDetectionStrategy,
  Component,
  Signal,
  WritableSignal,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import {
  AUDIO_STEP_IDS,
  ConsonantSoftenEngine,
  ConsonantSoftenPreset,
  PauseDurations,
  toApiError,
} from '@app/api';
import { HasUnsavedChanges } from '@app/shared/unsaved-changes.guard';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { AudioCard } from './audio-card';
import {
  ConsonantSoftenForm,
  MAX_HIGHPASS_HZ,
  MIN_HIGHPASS_HZ,
  SilenceTrimForm,
  buildConsonantSoftenConfig,
  buildSilenceTrimConfig,
  sameForm,
  setSoftenEngine,
  setSoftenPreset,
  toConsonantSoftenForm,
  toSilenceTrimForm,
  validateAttempts,
  validateChunkPause,
  validateConsonantSoften,
  validatePauses,
  validateSilenceTrim,
  validateWer,
} from './audio-forms';
import { AudioSettingsStore } from './audio-settings-store';
import { StepPreview } from './step-preview';

/**
 * One card's draft against the store's snapshot. The draft is re-seeded from a reload only while
 * it is untouched (equal to what it was last seeded from), so a change made in Blazor or over the
 * API lands here without wiping typing in progress. After a save the card marks itself untouched,
 * so the reload that follows seeds it with whatever the host normalised.
 */
class CardState<T> {
  readonly draft: WritableSignal<T>;
  readonly dirty: Signal<boolean>;
  readonly saving = signal(false);
  private seeded: T;

  constructor(
    readonly baseline: Signal<T>,
    readonly validate: (draft: T) => string | null,
  ) {
    this.seeded = baseline();
    this.draft = signal(this.seeded);
    this.dirty = computed(() => !sameForm(this.draft(), this.baseline()));
  }

  readonly error = computed(() => this.validate(this.draft()));

  /** Takes the baseline's current value, unless the draft has been touched since it was last seeded. */
  reseed(): void {
    const next = this.baseline();
    if (sameForm(this.draft(), this.seeded)) this.draft.set(next);
    this.seeded = next;
  }

  markSaved(): void {
    this.seeded = this.draft();
  }

  /** Immutable update of one field. */
  set<K extends keyof T>(key: K, value: T[K]): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }
}

/** A number input's value; NaN when blank, so validation catches it. */
export function num(event: Event): number {
  return (event.target as HTMLInputElement).valueAsNumber;
}

const ENGINE_OPTIONS: { value: ConsonantSoftenEngine; label: string }[] = [
  { value: 'adyneq', label: 'adynEQ (recommended)' },
  { value: 'deesser', label: 'deesser' },
];

const PRESET_OPTIONS: { value: ConsonantSoftenPreset; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'medium', label: 'Medium' },
  { value: 'strong', label: 'Strong' },
  { value: 'custom', label: 'Custom' },
];

interface AdynEqField {
  key: keyof ConsonantSoftenForm['adynEq'];
  label: string;
  step: number;
  min?: number;
}
interface DeesserField {
  key: keyof ConsonantSoftenForm['deesser'];
  label: string;
  step: number;
  min?: number;
  max?: number;
}

const ADYNEQ_FIELDS: AdynEqField[] = [
  { key: 'thresholdDb', label: 'Threshold (dB)', step: 1 },
  { key: 'ratio', label: 'Ratio', step: 0.5, min: 1 },
  { key: 'rangeDb', label: 'Range (dB)', step: 1 },
  { key: 'detectFrequencyHz', label: 'Detect frequency (Hz)', step: 100 },
  { key: 'detectQ', label: 'Detect Q', step: 0.1 },
  { key: 'targetFrequencyHz', label: 'Target frequency (Hz)', step: 100 },
  { key: 'targetQ', label: 'Target Q', step: 0.1 },
  { key: 'attackMs', label: 'Attack (ms)', step: 1, min: 0 },
  { key: 'releaseMs', label: 'Release (ms)', step: 5, min: 0 },
  { key: 'shelfFrequencyHz', label: 'Shelf frequency (Hz)', step: 100 },
  { key: 'shelfGainDb', label: 'Shelf gain (dB)', step: 0.5 },
];

const DEESSER_FIELDS: DeesserField[] = [
  { key: 'intensity', label: 'Intensity (0–1)', step: 0.05, min: 0, max: 1 },
  { key: 'makeupAmount', label: 'Makeup amount (0–1)', step: 0.05, min: 0, max: 1 },
  { key: 'frequency', label: 'Split frequency (0–1)', step: 0.05, min: 0, max: 1 },
  { key: 'shelfFrequencyHz', label: 'Shelf frequency (Hz)', step: 100 },
  { key: 'shelfGainDb', label: 'Shelf gain (dB)', step: 0.5 },
];

const PAUSE_FIELDS: { key: keyof PauseDurations; label: string; step: number }[] = [
  { key: 'volumeMs', label: 'Volume pause (ms)', step: 100 },
  { key: 'partMs', label: 'Part pause (ms)', step: 100 },
  { key: 'chapterMs', label: 'Chapter pause (ms)', step: 100 },
  { key: 'paragraphMs', label: 'Paragraph pause (ms)', step: 50 },
  { key: 'pauseMs', label: 'Pause (ms)', step: 50 },
];

/**
 * `/settings/audio` (ticket 24): the seven independently saved cards of Blazor's Audio Processing
 * page — ffmpeg, silence trim and consonant soften (each with an A/B preview of the unsaved
 * draft), chunk pause, pause durations, WER threshold, max audio attempts.
 */
@Component({
  selector: 'app-audio-settings-page',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    PageHeader,
    StatusChip,
    AudioCard,
    StepPreview,
  ],
  providers: [AudioSettingsStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header
      title="Audio processing"
      subtitle="ffmpeg, the paragraph post-process steps, assembly pauses and verification."
    />

    @if (store.error(); as error) {
      <r2m-status-chip status="error" [label]="'Audio settings could not be loaded: ' + error" />
    }

    @if (store.full()) {
      <!-- ffmpeg -->
      <app-audio-card
        card="ffmpeg"
        title="ffmpeg"
        blurb="Path to the ffmpeg executable used by the audio post-processing pipeline. Leave blank to rely on the system PATH."
        [dirty]="ffmpeg.dirty()"
        [saving]="ffmpeg.saving()"
        [error]="ffmpeg.error()"
        (save)="saveFfmpeg()"
      >
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>ffmpeg path</mat-label>
          <input
            matInput
            autocomplete="off"
            placeholder="(blank = rely on PATH)"
            data-field="ffmpegPath"
            [value]="ffmpeg.draft()"
            (input)="ffmpeg.draft.set($any($event.target).value)"
          />
        </mat-form-field>
        <button
          actions
          mat-stroked-button
          type="button"
          data-action="test-ffmpeg"
          [disabled]="testing() || ffmpeg.saving()"
          (click)="testFfmpeg()"
        >
          {{ testing() ? 'Testing…' : 'Test ffmpeg' }}
        </button>
      </app-audio-card>

      <!-- Silence trim -->
      <app-audio-card
        card="silence-trim"
        title="Silence trim"
        blurb="Strips leading and trailing dead air from generated paragraph audio, so the gap between items is the configured pause and nothing else. Runs before consonant soften. If the trim would remove nearly all of a clip it is skipped and the audio is kept as generated."
        [dirty]="trim.dirty()"
        [saving]="trim.saving()"
        [error]="trim.error()"
        (save)="saveTrim()"
      >
        <mat-slide-toggle
          data-field="trim-enabled"
          [checked]="trim.draft().enabled"
          (change)="trim.set('enabled', $event.checked)"
        >
          Enable silence trim
        </mat-slide-toggle>
        <div class="audio__row">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Silence threshold (dB)</mat-label>
            <input
              matInput
              type="number"
              max="0"
              step="1"
              data-field="trim-threshold"
              [value]="trim.draft().thresholdDb"
              (input)="trim.set('thresholdDb', num($event))"
            />
            <mat-hint>Peak level below which audio counts as silence. Lower is stricter.</mat-hint>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Keep at each end (ms)</mat-label>
            <input
              matInput
              type="number"
              min="0"
              step="10"
              data-field="trim-pad"
              [value]="trim.draft().padMs"
              (input)="trim.set('padMs', num($event))"
            />
            <mat-hint>Silence deliberately kept at each end. 0 = hard trim.</mat-hint>
          </mat-form-field>
        </div>
        <app-step-preview
          [stepId]="stepIds.silenceTrim"
          [settings]="trimConfig().settings"
          [canRender]="!trim.error()"
          processedLabel="Trimmed"
          showRemovedMs
        />
      </app-audio-card>

      <!-- Consonant soften -->
      <app-audio-card
        card="consonant-soften"
        title="Consonant soften"
        blurb="Optional ffmpeg filter applied to paragraph audio after loudness normalize and before verification — tames harsh sibilants and plosives. A filter failure falls back to the unfiltered audio."
        [dirty]="soften.dirty()"
        [saving]="soften.saving()"
        [error]="soften.error()"
        (save)="saveSoften()"
      >
        <mat-slide-toggle
          data-field="soften-enabled"
          [checked]="soften.draft().enabled"
          (change)="soften.set('enabled', $event.checked)"
        >
          Enable consonant soften
        </mat-slide-toggle>
        <div class="audio__row">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Engine</mat-label>
            <mat-select
              data-field="soften-engine"
              [value]="soften.draft().engine"
              (valueChange)="soften.draft.set(setEngine(soften.draft(), $event))"
            >
              @for (option of engineOptions; track option.value) {
                <mat-option [value]="option.value">{{ option.label }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Preset</mat-label>
            <mat-select
              data-field="soften-preset"
              [value]="soften.draft().preset"
              (valueChange)="soften.draft.set(setPreset(soften.draft(), $event))"
            >
              @for (option of presetOptions; track option.value) {
                <mat-option [value]="option.value">{{ option.label }}</mat-option>
              }
            </mat-select>
            <mat-hint
              >Picking a preset re-seeds the custom fields — unsaved tweaks are discarded.</mat-hint
            >
          </mat-form-field>
        </div>

        @if (soften.draft().engine === 'deesser') {
          <p class="audio__info" data-role="deesser-caveat">
            deesser intensity is sample-rate-sensitive: higher provider sample rates need a higher
            intensity to engage at all.
          </p>
        }

        @if (soften.draft().preset === 'custom') {
          <div class="audio__grid" data-role="custom-fields">
            @if (soften.draft().engine === 'adyneq') {
              @for (field of adynEqFields; track field.key) {
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>{{ field.label }}</mat-label>
                  <input
                    matInput
                    type="number"
                    [attr.data-field]="'adyneq-' + field.key"
                    [step]="field.step"
                    [attr.min]="field.min ?? null"
                    [value]="soften.draft().adynEq[field.key]"
                    (input)="setAdynEq(field.key, num($event))"
                  />
                </mat-form-field>
              }
            } @else {
              @for (field of deesserFields; track field.key) {
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>{{ field.label }}</mat-label>
                  <input
                    matInput
                    type="number"
                    [attr.data-field]="'deesser-' + field.key"
                    [step]="field.step"
                    [attr.min]="field.min ?? null"
                    [attr.max]="field.max ?? null"
                    [value]="soften.draft().deesser[field.key]"
                    (input)="setDeesser(field.key, num($event))"
                  />
                </mat-form-field>
              }
            }
          </div>
          <mat-slide-toggle
            data-field="soften-highpass"
            [checked]="soften.draft().highpassEnabled"
            (change)="soften.set('highpassEnabled', $event.checked)"
          >
            Highpass (removes plosive thump)
          </mat-slide-toggle>
          @if (soften.draft().highpassEnabled) {
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="audio__narrow">
              <mat-label>Highpass cutoff (Hz)</mat-label>
              <input
                matInput
                type="number"
                step="10"
                data-field="soften-highpass-hz"
                [min]="minHighpass"
                [max]="maxHighpass"
                [value]="soften.draft().highpassHz"
                (input)="soften.set('highpassHz', num($event))"
              />
            </mat-form-field>
          }
        }

        <app-step-preview
          [stepId]="stepIds.consonantSoften"
          [settings]="softenConfig().settings"
          [canRender]="!soften.error()"
          processedLabel="Filtered"
        />
      </app-audio-card>

      <!-- Chunk pause -->
      <app-audio-card
        card="chunk-pause"
        title="Chunk pause"
        blurb="Silence inserted between stitched audio chunks of a paragraph."
        [dirty]="chunk.dirty()"
        [saving]="chunk.saving()"
        [error]="chunk.error()"
        (save)="saveChunk()"
      >
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="audio__narrow">
          <mat-label>Pause between chunks (ms)</mat-label>
          <input
            matInput
            type="number"
            min="0"
            step="50"
            data-field="chunkPauseMs"
            [value]="chunk.draft()"
            (input)="chunk.draft.set(num($event))"
          />
        </mat-form-field>
      </app-audio-card>

      <!-- Pause durations -->
      <app-audio-card
        card="pauses"
        title="Pause durations"
        blurb="Silence inserted between sections during audiobook assembly. Each kind maps to a millisecond duration."
        [dirty]="pauses.dirty()"
        [saving]="pauses.saving()"
        [error]="pauses.error()"
        (save)="savePauses()"
      >
        <div class="audio__grid">
          @for (field of pauseFields; track field.key) {
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>{{ field.label }}</mat-label>
              <input
                matInput
                type="number"
                min="0"
                [step]="field.step"
                [attr.data-field]="field.key"
                [value]="pauses.draft()[field.key]"
                (input)="pauses.set(field.key, num($event))"
              />
            </mat-form-field>
          }
        </div>
      </app-audio-card>

      <!-- WER threshold -->
      <app-audio-card
        card="wer"
        title="Transcript verification"
        blurb="Word-error-rate threshold below which a generated clip passes verification."
        [dirty]="wer.dirty()"
        [saving]="wer.saving()"
        [error]="wer.error()"
        (save)="saveWer()"
      >
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="audio__narrow">
          <mat-label>WER threshold</mat-label>
          <input
            matInput
            type="number"
            min="0"
            max="1"
            step="0.01"
            data-field="werThreshold"
            [value]="wer.draft()"
            (input)="wer.draft.set(num($event))"
          />
        </mat-form-field>
      </app-audio-card>

      <!-- Max audio attempts -->
      <app-audio-card
        card="attempts"
        title="Max audio attempts"
        blurb="Total generation attempts per item, including the first. 1 = no retry."
        [dirty]="attempts.dirty()"
        [saving]="attempts.saving()"
        [error]="attempts.error()"
        (save)="saveAttempts()"
      >
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="audio__narrow">
          <mat-label>Max audio attempts</mat-label>
          <input
            matInput
            type="number"
            min="1"
            step="1"
            data-field="audioMaxAttempts"
            [value]="attempts.draft()"
            (input)="attempts.draft.set(num($event))"
          />
        </mat-form-field>
      </app-audio-card>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-4);
      max-width: 880px;
    }
    .audio__row,
    .audio__grid {
      display: grid;
      gap: var(--r2m-space-3);
      grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
    }
    .audio__narrow {
      max-width: 320px;
    }
    .audio__info {
      margin: 0;
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface-high, var(--r2m-surface-low));
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    mat-form-field {
      width: 100%;
    }
  `,
})
export class AudioSettingsPage implements HasUnsavedChanges {
  protected readonly store = inject(AudioSettingsStore);
  private readonly toast = inject(ToastService);

  protected readonly stepIds = AUDIO_STEP_IDS;
  protected readonly engineOptions = ENGINE_OPTIONS;
  protected readonly presetOptions = PRESET_OPTIONS;
  protected readonly adynEqFields = ADYNEQ_FIELDS;
  protected readonly deesserFields = DEESSER_FIELDS;
  protected readonly pauseFields = PAUSE_FIELDS;
  protected readonly minHighpass = MIN_HIGHPASS_HZ;
  protected readonly maxHighpass = MAX_HIGHPASS_HZ;
  protected readonly num = num;
  protected readonly setEngine = setSoftenEngine;
  protected readonly setPreset = setSoftenPreset;

  protected readonly testing = signal(false);

  private readonly trimStored = computed(() => this.store.step(AUDIO_STEP_IDS.silenceTrim));
  private readonly softenStored = computed(() => this.store.step(AUDIO_STEP_IDS.consonantSoften));

  protected readonly ffmpeg = new CardState<string>(
    computed(() => this.store.full()?.ffmpegPath ?? ''),
    () => null,
  );
  protected readonly trim = new CardState<SilenceTrimForm>(
    computed(() => toSilenceTrimForm(this.trimStored())),
    validateSilenceTrim,
  );
  protected readonly soften = new CardState<ConsonantSoftenForm>(
    computed(() => toConsonantSoftenForm(this.softenStored())),
    validateConsonantSoften,
  );
  protected readonly chunk = new CardState<number>(
    computed(() => this.store.full()?.chunkPauseMs ?? 300),
    validateChunkPause,
  );
  protected readonly pauses = new CardState<PauseDurations>(
    computed(
      () =>
        this.store.full()?.pauses ?? {
          volumeMs: 4000,
          partMs: 3000,
          chapterMs: 2500,
          paragraphMs: 800,
          pauseMs: 500,
        },
    ),
    validatePauses,
  );
  protected readonly wer = new CardState<number>(
    computed(() => this.store.full()?.werThreshold ?? 0.15),
    validateWer,
  );
  protected readonly attempts = new CardState<number>(
    computed(() => this.store.full()?.audioMaxAttempts ?? 1),
    validateAttempts,
  );

  private readonly cards = [
    this.ffmpeg,
    this.trim,
    this.soften,
    this.chunk,
    this.pauses,
    this.wer,
    this.attempts,
  ] as const;

  /** The draft as the host would store it — what Save writes and what the preview renders. */
  protected readonly trimConfig = computed(() =>
    buildSilenceTrimConfig(this.trim.draft(), this.trimStored()),
  );
  protected readonly softenConfig = computed(() => buildConsonantSoftenConfig(this.soften.draft()));

  constructor() {
    void this.store.load();
    effect(() => {
      const full = this.store.full();
      if (!full) return;
      untracked(() => this.reseed());
    });
  }

  hasUnsavedChanges(): boolean {
    return this.cards.some((c) => c.dirty());
  }

  private reseed(): void {
    for (const card of this.cards) card.reseed();
  }

  protected setAdynEq(key: keyof ConsonantSoftenForm['adynEq'], value: number): void {
    this.soften.draft.update((d) => ({ ...d, adynEq: { ...d.adynEq, [key]: value } }));
  }

  protected setDeesser(key: keyof ConsonantSoftenForm['deesser'], value: number): void {
    this.soften.draft.update((d) => ({ ...d, deesser: { ...d.deesser, [key]: value } }));
  }

  protected saveFfmpeg(): Promise<void> {
    return this.save(
      this.ffmpeg,
      () => this.store.saveScalars({ ffmpegPath: this.ffmpeg.draft() }),
      'ffmpeg path saved',
    );
  }

  protected saveTrim(): Promise<void> {
    return this.save(this.trim, () => this.store.saveStep(this.trimConfig()), 'Silence trim saved');
  }

  protected saveSoften(): Promise<void> {
    return this.save(
      this.soften,
      () => this.store.saveStep(this.softenConfig()),
      'Consonant soften saved',
    );
  }

  protected saveChunk(): Promise<void> {
    return this.save(
      this.chunk,
      () => this.store.saveScalars({ chunkPauseMs: this.chunk.draft() }),
      'Chunk pause saved',
    );
  }

  protected savePauses(): Promise<void> {
    return this.save(
      this.pauses,
      () => this.store.savePauses(this.pauses.draft()),
      'Pause durations saved',
    );
  }

  protected saveWer(): Promise<void> {
    return this.save(
      this.wer,
      () => this.store.saveScalars({ werThreshold: this.wer.draft() }),
      'WER threshold saved',
    );
  }

  protected saveAttempts(): Promise<void> {
    return this.save(
      this.attempts,
      () => this.store.saveScalars({ audioMaxAttempts: this.attempts.draft() }),
      'Max audio attempts saved',
    );
  }

  /** Probes the path currently in the field, persisting it first so the probe matches the UI. */
  protected async testFfmpeg(): Promise<void> {
    this.testing.set(true);
    try {
      const path = this.ffmpeg.draft();
      const result = await this.store.testFfmpeg(path);
      // The probe persisted the path, so the card is clean against the reloaded value.
      if (sameForm(this.ffmpeg.draft(), path)) this.ffmpeg.markSaved();
      if (result.success) this.toast.success(result.message);
      else this.toast.error(result.message);
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.testing.set(false);
    }
  }

  private async save<T>(
    card: CardState<T>,
    write: () => Promise<void>,
    success: string,
  ): Promise<void> {
    if (card.error() || card.saving()) return;
    card.saving.set(true);
    try {
      // Untouched from here on: the reload after the write seeds the card with the stored value.
      card.markSaved();
      await write();
      this.toast.success(success);
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      card.saving.set(false);
    }
  }
}
