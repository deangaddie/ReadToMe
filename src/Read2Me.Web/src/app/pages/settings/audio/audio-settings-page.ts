import { html, nothing } from 'lit-html';
import {
  AUDIO_STEP_IDS,
  type ConsonantSoftenEngine,
  type ConsonantSoftenPreset,
  type PauseDurations,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import type { HasUnsavedChanges } from '@app/core/router';
import { provide, use } from '@app/core/services';
import {
  type ReadonlySignal,
  type WritableSignal,
  computed,
  signal,
  untracked,
} from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { controlValue, field } from '@app/ui/field';
import { statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { audioCard } from './audio-card';
import {
  type ConsonantSoftenForm,
  MAX_HIGHPASS_HZ,
  MIN_HIGHPASS_HZ,
  type SilenceTrimForm,
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
import css from './audio-settings-page.css' with { type: 'text' };
import './step-preview';

adoptStyles(css);

/**
 * One card's draft against the store's snapshot. The draft is re-seeded from a reload only while
 * it is untouched (equal to what it was last seeded from), so a change made in another tab or over
 * the API lands here without wiping typing in progress. After a save the card marks itself
 * untouched, so the reload that follows seeds it with whatever the host normalised.
 */
export class CardState<T> {
  readonly draft: WritableSignal<T>;
  readonly dirty: ReadonlySignal<boolean>;
  readonly saving = signal(false);
  readonly error: ReadonlySignal<string | null>;
  private seeded: T;

  constructor(
    readonly baseline: ReadonlySignal<T>,
    readonly validate: (draft: T) => string | null,
  ) {
    this.seeded = baseline();
    this.draft = signal(this.seeded);
    this.dirty = computed(() => !sameForm(this.draft(), this.baseline()));
    this.error = computed(() => this.validate(this.draft()));
  }

  /** Takes the baseline's current value, unless the draft has been touched since it was last seeded. */
  reseed(): void {
    const next = this.baseline();
    if (sameForm(this.draft(), this.seeded)) this.draft.set(next);
    this.seeded = next;
  }

  /**
   * Marks the draft untouched, so the reload after a write seeds the card with the stored value.
   * Returns the undo for a failed write, which leaves the edit in place and dirty.
   */
  markSaved(): () => void {
    const previous = this.seeded;
    this.seeded = this.draft();
    return () => {
      this.seeded = previous;
    };
  }

  /** Immutable update of one field. */
  set<K extends keyof T>(key: K, value: T[K]): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }
}

/** The number typed into the input an event came from; NaN when blank, so validation catches it. */
export function numberValue(event: Event): number {
  const text = controlValue(event).trim();
  return text === '' ? NaN : Number(text);
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

interface NumberFieldOptions {
  id: string;
  label: string;
  value: number;
  step?: number;
  min?: number | undefined;
  max?: number | undefined;
  hint?: string;
  cls?: string;
  onInput: (e: Event) => void;
}

/** A labelled `<input type=number data-field=id>`; `value` renders as text so NaN shows blank. */
const numberField = (o: NumberFieldOptions) =>
  field(
    {
      id: `audio-${o.id}`,
      label: o.label,
      ...(o.hint === undefined ? {} : { hint: o.hint }),
      ...(o.cls === undefined ? {} : { cls: o.cls }),
    },
    (a) =>
      html`<input
        id=${a.id}
        aria-describedby=${a.describedBy}
        aria-invalid=${a.invalid}
        type="number"
        data-field=${o.id}
        step=${o.step ?? nothing}
        min=${o.min ?? nothing}
        max=${o.max ?? nothing}
        .value=${Number.isFinite(o.value) ? String(o.value) : ''}
        @input=${o.onInput}
      />`,
  );

const switchField = (
  id: string,
  checked: boolean,
  label: string,
  onChange: (on: boolean) => void,
) =>
  html`<label class="r2m-switch">
    <input
      type="checkbox"
      role="switch"
      data-field=${id}
      .checked=${checked}
      @change=${(e: Event) => onChange((e.target as HTMLInputElement).checked)}
    />
    ${label}
  </label>`;

/**
 * `/settings/audio` (ticket 24): the seven independently saved cards of the Audio Processing
 * settings — ffmpeg, silence trim and consonant soften (each with an A/B preview of the unsaved
 * draft), chunk pause, pause durations, WER threshold, max audio attempts. Provides the
 * {@link AudioSettingsStore}; the router's `guardUnsaved` asks {@link hasUnsavedChanges} before
 * leaving.
 */
export class AudioSettingsPage extends R2mElement implements HasUnsavedChanges {
  readonly store = provide(this, AudioSettingsStore, new AudioSettingsStore());
  private readonly toast = use(ToastService);

  readonly testing = signal(false);

  private readonly trimStored = computed(() => this.store.step(AUDIO_STEP_IDS.silenceTrim));
  private readonly softenStored = computed(() => this.store.step(AUDIO_STEP_IDS.consonantSoften));

  readonly ffmpeg = new CardState<string>(
    computed(() => this.store.full()?.ffmpegPath ?? ''),
    () => null,
  );
  readonly trim = new CardState<SilenceTrimForm>(
    computed(() => toSilenceTrimForm(this.trimStored())),
    validateSilenceTrim,
  );
  readonly soften = new CardState<ConsonantSoftenForm>(
    computed(() => toConsonantSoftenForm(this.softenStored())),
    validateConsonantSoften,
  );
  readonly chunk = new CardState<number>(
    computed(() => this.store.full()?.chunkPauseMs ?? 300),
    validateChunkPause,
  );
  readonly pauses = new CardState<PauseDurations>(
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
  readonly wer = new CardState<number>(
    computed(() => this.store.full()?.werThreshold ?? 0.15),
    validateWer,
  );
  readonly attempts = new CardState<number>(
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
  readonly trimConfig = computed(() =>
    buildSilenceTrimConfig(this.trim.draft(), this.trimStored()),
  );
  readonly softenConfig = computed(() => buildConsonantSoftenConfig(this.soften.draft()));

  protected override connected(): void {
    this.classList.add('audio');
    void this.store.open();
    this.effect(() => {
      const full = this.store.full();
      if (!full) return;
      untracked(() => this.reseed());
    });
    this.onDisconnect(() => this.store.close());
  }

  hasUnsavedChanges(): boolean {
    return this.cards.some((c) => c.dirty());
  }

  private reseed(): void {
    for (const card of this.cards) card.reseed();
  }

  protected template() {
    const error = this.store.error();
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Audio processing</h1>
        <p class="r2m-page-header__subtitle">
          ffmpeg, the paragraph post-process steps, assembly pauses and verification.
        </p>
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Audio settings could not be loaded: ${error}` })
          : nothing
      }
      ${
        this.store.full()
          ? html`${this.ffmpegCard()} ${this.trimCard()} ${this.softenCard()} ${this.chunkCard()}
            ${this.pausesCard()} ${this.werCard()} ${this.attemptsCard()}`
          : nothing
      }
    `;
  }

  private ffmpegCard() {
    const card = this.ffmpeg;
    const testing = this.testing();
    return audioCard(
      {
        card: 'ffmpeg',
        title: 'ffmpeg',
        blurb:
          'Path to the ffmpeg executable used by the audio post-processing pipeline. Leave blank to rely on the system PATH.',
        state: card,
        onSave: () => void this.saveFfmpeg(),
        actions: html`<button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="test-ffmpeg"
          ?disabled=${testing || card.saving()}
          @click=${() => void this.testFfmpeg()}
        >
          ${testing ? 'Testing…' : 'Test ffmpeg'}
        </button>`,
      },
      field(
        { id: 'audio-ffmpegPath', label: 'ffmpeg path' },
        (a) =>
          html`<input
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            type="text"
            autocomplete="off"
            placeholder="(blank = rely on PATH)"
            data-field="ffmpegPath"
            .value=${card.draft()}
            @input=${(e: Event) => card.draft.set(controlValue(e))}
          />`,
      ),
    );
  }

  private trimCard() {
    const card = this.trim;
    const draft = card.draft();
    return audioCard(
      {
        card: 'silence-trim',
        title: 'Silence trim',
        blurb:
          'Strips leading and trailing dead air from generated paragraph audio, so the gap between items is the configured pause and nothing else. Runs before consonant soften. If the trim would remove nearly all of a clip it is skipped and the audio is kept as generated.',
        state: card,
        onSave: () => void this.saveTrim(),
      },
      html`
        ${switchField('trim-enabled', draft.enabled, 'Enable silence trim', (on) =>
          card.set('enabled', on),
        )}
        <div class="audio__row">
          ${numberField({
            id: 'trim-threshold',
            label: 'Silence threshold (dB)',
            value: draft.thresholdDb,
            max: 0,
            step: 1,
            hint: 'Peak level below which audio counts as silence. Lower is stricter.',
            onInput: (e) => card.set('thresholdDb', numberValue(e)),
          })}
          ${numberField({
            id: 'trim-pad',
            label: 'Keep at each end (ms)',
            value: draft.padMs,
            min: 0,
            step: 10,
            hint: 'Silence deliberately kept at each end. 0 = hard trim.',
            onInput: (e) => card.set('padMs', numberValue(e)),
          })}
        </div>
        <r2m-step-preview
          .stepId=${AUDIO_STEP_IDS.silenceTrim}
          .settings=${this.trimConfig().settings}
          .canRender=${!card.error()}
          .processedLabel=${'Trimmed'}
          .showRemovedMs=${true}
        ></r2m-step-preview>
      `,
    );
  }

  private softenCard() {
    const card = this.soften;
    const draft = card.draft();
    return audioCard(
      {
        card: 'consonant-soften',
        title: 'Consonant soften',
        blurb:
          'Optional ffmpeg filter applied to paragraph audio after loudness normalize and before verification — tames harsh sibilants and plosives. A filter failure falls back to the unfiltered audio.',
        state: card,
        onSave: () => void this.saveSoften(),
      },
      html`
        ${switchField('soften-enabled', draft.enabled, 'Enable consonant soften', (on) =>
          card.set('enabled', on),
        )}
        <div class="audio__row">
          ${field(
            { id: 'audio-soften-engine', label: 'Engine' },
            (a) =>
              html`<select
                id=${a.id}
                aria-describedby=${a.describedBy}
                data-field="soften-engine"
                .value=${draft.engine}
                @change=${(e: Event) =>
                  card.draft.set(
                    setSoftenEngine(card.draft(), controlValue(e) as ConsonantSoftenEngine),
                  )}
              >
                ${ENGINE_OPTIONS.map(
                  (o) =>
                    html`<option value=${o.value} ?selected=${o.value === draft.engine}>
                      ${o.label}
                    </option>`,
                )}
              </select>`,
          )}
          ${field(
            {
              id: 'audio-soften-preset',
              label: 'Preset',
              hint: 'Picking a preset re-seeds the custom fields — unsaved tweaks are discarded.',
            },
            (a) =>
              html`<select
                id=${a.id}
                aria-describedby=${a.describedBy}
                data-field="soften-preset"
                .value=${draft.preset}
                @change=${(e: Event) =>
                  card.draft.set(
                    setSoftenPreset(card.draft(), controlValue(e) as ConsonantSoftenPreset),
                  )}
              >
                ${PRESET_OPTIONS.map(
                  (o) =>
                    html`<option value=${o.value} ?selected=${o.value === draft.preset}>
                      ${o.label}
                    </option>`,
                )}
              </select>`,
          )}
        </div>

        ${
          draft.engine === 'deesser'
            ? html`<p class="audio__info" data-role="deesser-caveat">
                deesser intensity is sample-rate-sensitive: higher provider sample rates need a
                higher intensity to engage at all.
              </p>`
            : nothing
        }
        ${draft.preset === 'custom' ? this.customFields(draft) : nothing}

        <r2m-step-preview
          .stepId=${AUDIO_STEP_IDS.consonantSoften}
          .settings=${this.softenConfig().settings}
          .canRender=${!card.error()}
          .processedLabel=${'Filtered'}
        ></r2m-step-preview>
      `,
    );
  }

  private customFields(draft: ConsonantSoftenForm) {
    const card = this.soften;
    return html`
      <div class="audio__grid" data-role="custom-fields">
        ${
          draft.engine === 'adyneq'
            ? ADYNEQ_FIELDS.map((f) =>
                numberField({
                  id: `adyneq-${f.key}`,
                  label: f.label,
                  value: draft.adynEq[f.key],
                  step: f.step,
                  min: f.min,
                  onInput: (e) => this.setAdynEq(f.key, numberValue(e)),
                }),
              )
            : DEESSER_FIELDS.map((f) =>
                numberField({
                  id: `deesser-${f.key}`,
                  label: f.label,
                  value: draft.deesser[f.key],
                  step: f.step,
                  min: f.min,
                  max: f.max,
                  onInput: (e) => this.setDeesser(f.key, numberValue(e)),
                }),
              )
        }
      </div>
      ${switchField(
        'soften-highpass',
        draft.highpassEnabled,
        'Highpass (removes plosive thump)',
        (on) => card.set('highpassEnabled', on),
      )}
      ${
        draft.highpassEnabled
          ? numberField({
              id: 'soften-highpass-hz',
              label: 'Highpass cutoff (Hz)',
              value: draft.highpassHz,
              step: 10,
              min: MIN_HIGHPASS_HZ,
              max: MAX_HIGHPASS_HZ,
              cls: 'audio__narrow',
              onInput: (e) => card.set('highpassHz', numberValue(e)),
            })
          : nothing
      }
    `;
  }

  private chunkCard() {
    const card = this.chunk;
    return audioCard(
      {
        card: 'chunk-pause',
        title: 'Chunk pause',
        blurb: 'Silence inserted between stitched audio chunks of a paragraph.',
        state: card,
        onSave: () => void this.saveChunk(),
      },
      numberField({
        id: 'chunkPauseMs',
        label: 'Pause between chunks (ms)',
        value: card.draft(),
        min: 0,
        step: 50,
        cls: 'audio__narrow',
        onInput: (e) => card.draft.set(numberValue(e)),
      }),
    );
  }

  private pausesCard() {
    const card = this.pauses;
    const draft = card.draft();
    return audioCard(
      {
        card: 'pauses',
        title: 'Pause durations',
        blurb:
          'Silence inserted between sections during audiobook assembly. Each kind maps to a millisecond duration.',
        state: card,
        onSave: () => void this.savePauses(),
      },
      html`<div class="audio__grid">
        ${PAUSE_FIELDS.map((f) =>
          numberField({
            id: f.key,
            label: f.label,
            value: draft[f.key],
            min: 0,
            step: f.step,
            onInput: (e) => card.set(f.key, numberValue(e)),
          }),
        )}
      </div>`,
    );
  }

  private werCard() {
    const card = this.wer;
    return audioCard(
      {
        card: 'wer',
        title: 'Transcript verification',
        blurb: 'Word-error-rate threshold below which a generated clip passes verification.',
        state: card,
        onSave: () => void this.saveWer(),
      },
      numberField({
        id: 'werThreshold',
        label: 'WER threshold',
        value: card.draft(),
        min: 0,
        max: 1,
        step: 0.01,
        cls: 'audio__narrow',
        onInput: (e) => card.draft.set(numberValue(e)),
      }),
    );
  }

  private attemptsCard() {
    const card = this.attempts;
    return audioCard(
      {
        card: 'attempts',
        title: 'Max audio attempts',
        blurb: 'Total generation attempts per item, including the first. 1 = no retry.',
        state: card,
        onSave: () => void this.saveAttempts(),
      },
      numberField({
        id: 'audioMaxAttempts',
        label: 'Max audio attempts',
        value: card.draft(),
        min: 1,
        step: 1,
        cls: 'audio__narrow',
        onInput: (e) => card.draft.set(numberValue(e)),
      }),
    );
  }

  private setAdynEq(key: keyof ConsonantSoftenForm['adynEq'], value: number): void {
    this.soften.draft.update((d) => ({ ...d, adynEq: { ...d.adynEq, [key]: value } }));
  }

  private setDeesser(key: keyof ConsonantSoftenForm['deesser'], value: number): void {
    this.soften.draft.update((d) => ({ ...d, deesser: { ...d.deesser, [key]: value } }));
  }

  private saveFfmpeg(): Promise<void> {
    return this.save(
      this.ffmpeg,
      () => this.store.saveScalars({ ffmpegPath: this.ffmpeg.draft() }),
      'ffmpeg path saved',
    );
  }

  private saveTrim(): Promise<void> {
    return this.save(this.trim, () => this.store.saveStep(this.trimConfig()), 'Silence trim saved');
  }

  private saveSoften(): Promise<void> {
    return this.save(
      this.soften,
      () => this.store.saveStep(this.softenConfig()),
      'Consonant soften saved',
    );
  }

  private saveChunk(): Promise<void> {
    return this.save(
      this.chunk,
      () => this.store.saveScalars({ chunkPauseMs: this.chunk.draft() }),
      'Chunk pause saved',
    );
  }

  private savePauses(): Promise<void> {
    return this.save(
      this.pauses,
      () => this.store.savePauses(this.pauses.draft()),
      'Pause durations saved',
    );
  }

  private saveWer(): Promise<void> {
    return this.save(
      this.wer,
      () => this.store.saveScalars({ werThreshold: this.wer.draft() }),
      'WER threshold saved',
    );
  }

  private saveAttempts(): Promise<void> {
    return this.save(
      this.attempts,
      () => this.store.saveScalars({ audioMaxAttempts: this.attempts.draft() }),
      'Max audio attempts saved',
    );
  }

  /** Probes the path currently in the field, persisting it first so the probe matches the UI. */
  private async testFfmpeg(): Promise<void> {
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
    // Untouched from here on, so the reload inside the write seeds the card with the stored value.
    const undo = card.markSaved();
    try {
      await write();
      this.toast.success(success);
    } catch (e) {
      // Nothing was stored: the edit stays, and stays dirty against the next reload.
      undo();
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      card.saving.set(false);
    }
  }
}
define('r2m-audio-settings-page', AudioSettingsPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-audio-settings-page': AudioSettingsPage;
  }
}
