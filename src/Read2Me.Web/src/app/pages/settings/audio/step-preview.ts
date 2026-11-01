import { html, nothing } from 'lit-html';
import {
  AudioProcessingApi,
  type RecentAudioSampleDto,
  type StepPreviewResponse,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { icon, spinner } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { SamplePicker, provenance, snippet } from './sample-picker-dialog';
import css from './step-preview.css' with { type: 'text' };
import '@app/ui/audio-player';

adoptStyles(css);

/**
 * A post-process card's A/B preview (ticket 24): pick a recent sample, render the card's
 * *unsaved* settings over its Preview Source, then hear off-vs-draft on one A/B player. Renders
 * are on demand — the draft is only pushed through ffmpeg when asked, never per keystroke — and
 * a new sample drops the parked render rather than pairing it with the wrong original. An element:
 * it owns the picked sample and the render.
 */
export class StepPreview extends R2mElement {
  private readonly api = use(AudioProcessingApi);
  private readonly picker = use(SamplePicker);
  private readonly toast = use(ToastService);

  readonly #stepId = signal('');
  get stepId() {
    return this.#stepId();
  }
  set stepId(value: string) {
    this.#stepId.set(value);
  }

  /** The card's unsaved settings payload — what a render sends. */
  readonly #settings = signal<Record<string, unknown> | null>(null);
  get settings() {
    return this.#settings();
  }
  set settings(value: Record<string, unknown> | null) {
    this.#settings.set(value);
  }

  readonly #processedLabel = signal('Processed');
  get processedLabel() {
    return this.#processedLabel();
  }
  set processedLabel(value: string) {
    this.#processedLabel.set(value);
  }

  /** Cards that trim report how much audio the step removed. */
  readonly #showRemovedMs = signal(false);
  get showRemovedMs() {
    return this.#showRemovedMs();
  }
  set showRemovedMs(value: boolean) {
    this.#showRemovedMs.set(value);
  }

  /** False while the card's draft is invalid: nothing to audition yet. */
  readonly #canRender = signal(true);
  get canRender() {
    return this.#canRender();
  }
  set canRender(value: boolean) {
    this.#canRender.set(value);
  }

  readonly sample = signal<RecentAudioSampleDto | null>(null);
  readonly result = signal<StepPreviewResponse | null>(null);
  readonly rendering = signal(false);

  protected override connected(): void {
    this.classList.add('step-preview');
  }

  protected template() {
    const sample = this.sample();
    const result = this.result();
    const rendering = this.rendering();
    return html`
      <div class="step-preview__head">
        <span class="step-preview__title">A/B preview</span>
        ${
          sample
            ? html`<span class="step-preview__sample" data-role="sample">
                <span class="step-preview__snippet">${snippet(sample.text)}</span>
                <span class="step-preview__meta">${provenance(sample)}</span>
              </span>`
            : nothing
        }
        <span class="step-preview__actions">
          <button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="pick-sample"
            ?disabled=${rendering}
            @click=${() => void this.pick()}
          >
            ${sample ? 'Change sample' : 'Pick sample'}
          </button>
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            data-action="render-preview"
            ?disabled=${!sample || rendering || !this.#canRender()}
            @click=${() => void this.render()}
          >
            ${rendering ? html`${spinner(16)} Rendering…` : 'Render preview'}
          </button>
        </span>
      </div>

      ${
        result
          ? html`
              <r2m-audio-player
                .src=${result.originalUrl}
                .srcB=${result.processedUrl}
                .cacheKey=${result.previewId}
                .labelA=${'Original'}
                .labelB=${this.#processedLabel()}
                .label=${sample?.characterName ?? 'Narration'}
              ></r2m-audio-player>
              ${
                this.#showRemovedMs() && result.removedMs !== null
                  ? html`<p class="step-preview__note" data-role="removed">
                      Removed ${Math.round(result.removedMs)} ms
                    </p>`
                  : nothing
              }
              ${
                result.appliedOk
                  ? nothing
                  : html`<p class="step-preview__reason" role="alert" data-role="reason">
                      ${icon('info')}
                      ${result.reason ?? 'The step fell back to the unprocessed audio.'}
                    </p>`
              }
            `
          : nothing
      }
    `;
  }

  private async pick(): Promise<void> {
    const picked = await this.picker.pick();
    if (!picked) return;
    this.sample.set(picked);
    // The parked preview belongs to the previous sample.
    this.result.set(null);
  }

  private async render(): Promise<void> {
    const sample = this.sample();
    if (!sample || this.rendering()) return;
    this.rendering.set(true);
    try {
      this.result.set(
        await this.api.previewStep(this.#stepId(), {
          sample: { folder: sample.folder, itemId: sample.itemId },
          settings: this.#settings(),
        }),
      );
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.rendering.set(false);
    }
  }
}
define('r2m-step-preview', StepPreview);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-step-preview': StepPreview;
  }
}
