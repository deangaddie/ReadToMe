import { html, nothing } from 'lit-html';
import {
  SemanticSimilaritySettingsApi,
  type SimilarityTestResponse,
  TranscriptionSettingsApi,
  VoiceDesignSettingsApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { controlValue, field } from '@app/ui/field';
import type { RejectedFile } from '@app/ui/file-drop';
import { icon, statusChip } from '@app/ui/partials';
import type { ProviderConfig } from './provider-form';
import '@app/ui/audio-player';
import '@app/ui/file-drop';

/**
 * One Test action against a *saved* config. `run` reports a failure — a 422 carries the provider's
 * own reason — and drops the answer of a run whose config has since changed. Set `config`.
 */
abstract class TestPanel<TResult> extends R2mElement {
  readonly #config = signal<ProviderConfig | null>(null);
  get config() {
    return this.#config();
  }
  set config(value: ProviderConfig | null) {
    this.#config.set(value);
  }

  protected readonly running = signal(false);
  protected readonly result = signal<TResult | null>(null);
  protected readonly failure = signal<string | null>(null);

  private generation = 0;

  protected override connected(): void {
    // Another config is another test: whatever ran, or is still running, belongs to the last one.
    const configId = computed(() => this.#config()?.id ?? null);
    this.effect(() => {
      configId();
      untracked(() => {
        this.generation++;
        this.running.set(false);
        this.result.set(null);
        this.failure.set(null);
      });
    });
  }

  protected heading() {
    return html`<h2 class="test__title">Test "${this.#config()?.name ?? ''}"</h2>`;
  }

  protected progress() {
    return this.running()
      ? html`<progress class="test__progress" aria-label="Testing"></progress>`
      : nothing;
  }

  protected failureChip() {
    const failure = this.failure();
    return failure ? statusChip({ status: 'error', label: failure }) : nothing;
  }

  protected async run(action: (configId: number) => Promise<TResult>): Promise<void> {
    const config = this.#config();
    if (!config) return;
    const generation = this.generation;
    this.running.set(true);
    this.result.set(null);
    this.failure.set(null);
    try {
      const result = await action(config.id);
      if (generation === this.generation) this.result.set(result);
    } catch (e) {
      if (generation === this.generation) this.failure.set(`Test failed: ${toApiError(e).message}`);
    } finally {
      if (generation === this.generation) this.running.set(false);
    }
  }
}

/** Voice design: a prompt in, the designed voice speaking the sample text out. */
export class VoiceDesignTest extends TestPanel<string> {
  private readonly api = use(VoiceDesignSettingsApi);

  readonly prompt = signal('');

  protected template() {
    const running = this.running();
    /** The answer as a data URI the player can take. */
    const audioSrc = this.result();
    return html`
      ${this.heading()}
      <p class="test__note">
        Designs a voice with the saved configuration; it can take up to a minute.
      </p>
      <div class="test__row">
        ${field(
          { id: 'voice-design-test-prompt', label: 'Test prompt', cls: 'test__grow' },
          (a) =>
            html`<input
              id=${a.id}
              aria-describedby=${a.describedBy}
              aria-invalid=${a.invalid}
              type="text"
              autocomplete="off"
              data-field="prompt"
              placeholder="A warm, gravelly old man"
              ?disabled=${running}
              .value=${this.prompt()}
              @input=${(e: Event) => this.prompt.set(controlValue(e))}
            />`,
        )}
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="test"
          ?disabled=${running || !this.prompt().trim()}
          @click=${() => void this.test()}
        >
          ${icon('graphic_eq')} Test design
        </button>
      </div>
      ${this.progress()}
      ${
        audioSrc
          ? html`<r2m-audio-player
              .compact=${true}
              .label=${'Designed voice'}
              .src=${audioSrc}
            ></r2m-audio-player>`
          : nothing
      }
      ${this.failureChip()}
    `;
  }

  private test(): Promise<void> {
    const prompt = this.prompt().trim();
    return this.run(async (id) => {
      const audio = await this.api.test(id, prompt);
      return `data:${audio.contentType};base64,${audio.audioBase64}`;
    });
  }
}
define('r2m-voice-design-test', VoiceDesignTest);

const MAX_TEST_AUDIO_BYTES = 50 * 1024 * 1024;

/** Transcription: an audio file in, its transcript out. */
export class TranscriptionTest extends TestPanel<string> {
  private readonly api = use(TranscriptionSettingsApi);

  protected template() {
    const result = this.result();
    return html`
      ${this.heading()}
      <p class="test__note">Transcribes an audio file with the saved configuration.</p>
      <r2m-file-drop
        .accept=${'.wav,.mp3,.aac'}
        .label=${'Drop an audio file here'}
        .hint=${'WAV, MP3 or AAC, up to 50 MB'}
        .maxBytes=${MAX_TEST_AUDIO_BYTES}
        @files=${(e: CustomEvent<File[]>) => this.test(e.detail)}
        @rejected=${(e: CustomEvent<RejectedFile[]>) => this.reject(e.detail)}
      ></r2m-file-drop>
      ${this.progress()}
      ${
        result !== null
          ? html`<div role="status" data-role="transcript">
              <p class="test__note">Transcript</p>
              <p class="test__result">${result || '(empty)'}</p>
            </div>`
          : nothing
      }
      ${this.failureChip()}
    `;
  }

  private test(files: File[]): void {
    const [audio] = files;
    if (audio && !this.running()) void this.run((id) => this.api.test(id, audio));
  }

  private reject(rejected: RejectedFile[]): void {
    this.result.set(null);
    this.failure.set(
      rejected[0]?.reason === 'size'
        ? 'Audio exceeds the 50 MB limit.'
        : 'Unsupported format. Use WAV, MP3 or AAC.',
    );
  }
}
define('r2m-transcription-test', TranscriptionTest);

/** Similarity: two texts in, the score against the config's pass threshold out. */
export class SimilarityTest extends TestPanel<SimilarityTestResponse> {
  private readonly api = use(SemanticSimilaritySettingsApi);

  readonly text1 = signal('');
  readonly text2 = signal('');
  readonly canCompare = computed(
    () => !this.running() && !!this.text1().trim() && !!this.text2().trim(),
  );

  protected template() {
    const result = this.result();
    return html`
      ${this.heading()}
      <p class="test__note">Compares two texts with the saved configuration.</p>
      <div class="test__row">
        ${this.textField('text1', 'Text 1', this.text1)}
        ${this.textField('text2', 'Text 2', this.text2)}
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="test"
          ?disabled=${!this.canCompare()}
          @click=${() => void this.test()}
        >
          ${icon('compare_arrows')} Compare
        </button>
      </div>
      ${this.progress()}
      ${
        result
          ? html`<span data-role="score"
              >${statusChip({
                status: result.pass ? 'ok' : 'warn',
                label: `Score ${result.score.toFixed(3)} — ${result.pass ? 'PASS' : 'FAIL'} (threshold ${result.threshold.toFixed(2)})`,
              })}</span
            >`
          : nothing
      }
      ${this.failureChip()}
    `;
  }

  private textField(key: 'text1' | 'text2', label: string, value: typeof this.text1) {
    return field(
      { id: `similarity-test-${key}`, label, cls: 'test__grow' },
      (a) =>
        html`<input
          id=${a.id}
          aria-describedby=${a.describedBy}
          aria-invalid=${a.invalid}
          type="text"
          autocomplete="off"
          data-field=${key}
          .value=${value()}
          @input=${(e: Event) => value.set(controlValue(e))}
        />`,
    );
  }

  private test(): Promise<void> {
    const [text1, text2] = [this.text1().trim(), this.text2().trim()];
    return this.run((id) => this.api.test(id, text1, text2));
  }
}
define('r2m-similarity-test', SimilarityTest);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-design-test': VoiceDesignTest;
    'r2m-transcription-test': TranscriptionTest;
    'r2m-similarity-test': SimilarityTest;
  }
}
