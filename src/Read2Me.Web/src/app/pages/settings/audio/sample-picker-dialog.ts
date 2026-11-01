import { html } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { AudioProcessingApi, type RecentAudioSampleDto, toApiError } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { spinner } from '@app/ui/partials';
import css from './sample-picker-dialog.css' with { type: 'text' };

adoptStyles(css);

export const SAMPLE_LIMIT = 20;
const SNIPPET_LENGTH = 90;

export function snippet(text: string): string {
  return text.length <= SNIPPET_LENGTH ? text : `${text.slice(0, SNIPPET_LENGTH)}…`;
}

/** "Elizabeth · Elizabeth Voice · Pride and Prejudice" — character (or Narration), voice when known, project. */
export function provenance(sample: RecentAudioSampleDto): string {
  return [sample.characterName ?? 'Narration', sample.voiceName, sample.projectTitle]
    .filter((part): part is string => !!part)
    .join(' · ');
}

/**
 * The A/B preview's sample picker (ticket 24): the most recently generated paragraph items that
 * still hold a Preview Source, newest first. Picking a row closes with it.
 */
export class SamplePickerDialog extends R2mElement {
  private readonly api = use(AudioProcessingApi);

  readonly samples = signal<RecentAudioSampleDto[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  protected override connected(): void {
    void this.load();
  }

  protected template() {
    return html`
      <h2 class="r2m-dialog__title">Pick a sample</h2>
      <div class="r2m-dialog__content sample-picker">${this.body()}</div>
      <div class="r2m-dialog__actions">
        <button
          type="button"
          class="r2m-button sample-picker__cancel"
          @click=${() => this.emit('r2m-close', null)}
        >
          Cancel
        </button>
      </div>
    `;
  }

  private body() {
    const error = this.error();
    const samples = this.samples();
    if (this.loading()) return spinner(24);
    if (error)
      return html`<p class="sample-picker__empty" role="alert">
        Samples could not be loaded: ${error}
      </p>`;
    if (!samples.length)
      return html`<p class="sample-picker__empty" data-role="empty">
        No preview samples yet — generate some audio and it'll show up here.
      </p>`;
    return html`<ul class="sample-picker__list">
      ${repeat(
        samples,
        (sample) => sample.itemId,
        (sample) =>
          html`<li>
            <button
              type="button"
              class="sample-picker__row"
              data-item=${sample.itemId}
              @click=${() => this.emit('r2m-close', sample)}
            >
              <span class="sample-picker__text">${snippet(sample.text)}</span>
              <span class="sample-picker__meta">${provenance(sample)}</span>
            </button>
          </li>`,
      )}
    </ul>`;
  }

  private async load(): Promise<void> {
    try {
      this.samples.set(await this.api.recentSamples(SAMPLE_LIMIT));
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.loading.set(false);
    }
  }
}
define('r2m-sample-picker-dialog', SamplePickerDialog);

/** Opens the picker; a spec overrides it to answer without a dialog. */
export class SamplePicker {
  /** Resolves the picked sample, or null when dismissed. */
  async pick(): Promise<RecentAudioSampleDto | null> {
    const dialog = document.createElement('r2m-sample-picker-dialog');
    return (await openDialog<RecentAudioSampleDto | null>(dialog)) ?? null;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-sample-picker-dialog': SamplePickerDialog;
  }
}
