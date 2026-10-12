import { html, nothing } from 'lit-html';
import { type VoiceDesignSampleText, VoiceDesignSettingsApi, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { LiveService } from '@app/live/live.service';
import { field } from '@app/ui/field';
import { ToastService } from '@app/ui/toast';

/**
 * The sentence every designed voice speaks (native-web 35): Save is dirty-gated, Reset to default only
 * refills the draft — and saving the default text stores "no override". Reloads on the hub's
 * `settingsChanged { area: 'voice-design' }` while connected; `dirty()` tells the page whether
 * edits would be lost.
 */
export class VoiceDesignSampleTextCard extends R2mElement {
  private readonly api = use(VoiceDesignSettingsApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

  /** Null until the first load lands. */
  readonly stored = signal<VoiceDesignSampleText | null>(null);
  readonly draft = signal('');
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  private readonly effective = computed(() => {
    const stored = this.stored();
    return stored ? (stored.text ?? stored.default) : '';
  });
  readonly dirty = computed(() => this.stored() !== null && this.draft() !== this.effective());
  /** Over the host's cap: the host would refuse it, so Save does not offer it. */
  readonly overLimit = computed(() => {
    const stored = this.stored();
    return stored !== null && this.draft().length > stored.maxLength;
  });
  readonly isDefault = computed(() => this.draft() === (this.stored()?.default ?? ''));

  protected override connected(): void {
    this.classList.add('sample');
    void this.load();
    this.onDisconnect(
      this.live.on('settingsChanged', (m) => {
        if (m.area === 'voice-design') void this.load();
      }),
    );
  }

  protected template() {
    const stored = this.stored();
    const saving = this.saving();
    const error = this.error();
    const over = this.overLimit();
    // A save is in flight: Save is off until it lands.
    if (saving) this.setAttribute('aria-busy', 'true');
    else this.removeAttribute('aria-busy');
    return html`
      <h2 class="sample__title">Sample text</h2>
      <p class="sample__note">The sentence every designed voice speaks.</p>
      ${field(
        {
          id: 'voice-design-sample-text',
          label: 'Sample text',
          cls: 'sample__text',
          hint: stored
            ? html`<span class="sample__hint">
                <span>Kept short so every designed voice lands within the Reference Limit.</span>
                <span data-testid="sample-text-count" class=${over ? 'sample__over' : ''}
                  >${this.draft().length} / ${stored.maxLength}</span
                >
              </span>`
            : null,
        },
        (a) =>
          html`<textarea
            id=${a.id}
            aria-describedby=${a.describedBy}
            aria-invalid=${a.invalid}
            data-field="sampleText"
            ?disabled=${stored === null || saving}
            .value=${this.draft()}
            @input=${(e: Event) => this.draft.set((e.target as HTMLTextAreaElement).value)}
          ></textarea>`,
      )}
      <div class="sample__actions">
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="save-sample-text"
          ?disabled=${!this.dirty() || over || saving}
          @click=${() => void this.save()}
        >
          Save
        </button>
        <button
          type="button"
          class="r2m-button"
          data-action="reset-sample-text"
          ?disabled=${this.isDefault() || saving}
          @click=${() => this.resetToDefault()}
        >
          Reset to default
        </button>
      </div>
      ${error ? html`<p class="sample__error" role="alert">${error}</p>` : nothing}
    `;
  }

  private async load(): Promise<void> {
    try {
      this.accept(await this.api.sampleText());
    } catch (e) {
      this.error.set(`The sample text could not be loaded: ${toApiError(e).message}`);
    }
  }

  /** A reload never wipes a draft in progress. */
  private accept(stored: VoiceDesignSampleText, replaceDraft = !this.dirty()): void {
    this.stored.set(stored);
    if (replaceDraft) this.draft.set(stored.text ?? stored.default);
    this.error.set(null);
  }

  private resetToDefault(): void {
    this.draft.set(this.stored()?.default ?? '');
  }

  private async save(): Promise<void> {
    this.saving.set(true);
    try {
      this.accept(await this.api.setSampleText(this.draft()), true);
      this.toast.success('Sample text saved');
    } catch (e) {
      this.error.set(toApiError(e).message);
    } finally {
      this.saving.set(false);
    }
  }
}
define('r2m-voice-design-sample-text', VoiceDesignSampleTextCard);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-design-sample-text': VoiceDesignSampleTextCard;
  }
}
