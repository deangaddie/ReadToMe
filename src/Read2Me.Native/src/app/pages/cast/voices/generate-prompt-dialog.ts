import { html, nothing } from 'lit-html';
import { ActivityStore } from '@app/activity/activity-store';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import { type Guid, VoicesApi, toApiError } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { icon } from '@app/ui/partials';
import { streamDisclosure } from '@app/ui/stream-disclosure';

export interface GeneratePromptDialogData {
  folder: string;
  characterId: Guid;
  characterName: string;
}

export interface GeneratePromptDialogResult {
  designPrompt: string;
}

export type GeneratePromptPhase = 'rendering' | 'edit' | 'generating';

/** The activity centre's id for the single voice-prompt run (one at a time). */
export const VOICE_PROMPT_JOB_ID = 'voicePrompt';

/**
 * "Regenerate with AI" (research §4): the host renders the voice-design prompt template
 * for the character, the user reviews or edits it, Send to AI asks the LLM and the answer comes
 * back as the card's prompt draft. The LLM stream is inline while generating, and the run is
 * registered with the activity centre so the pill shows it. Cancel while generating closes the
 * dialog; the host finishes on its own and the answer is dropped.
 */
export class GeneratePromptDialog extends R2mElement {
  private readonly feed = use(LlmStreamFeed);
  private readonly voices = use(VoicesApi);
  private readonly activity = use(ActivityStore);

  data!: GeneratePromptDialogData;

  readonly phase = signal<GeneratePromptPhase>('rendering');
  readonly prompt = signal('');
  readonly error = signal<string | null>(null);
  readonly showStream = signal(false);
  readonly canSend = computed(() => this.phase() === 'edit' && this.prompt().trim().length > 0);

  /** Which host call is current; an answer from an earlier one is dropped. */
  private run = 0;
  private unregisterJob: (() => void) | null = null;

  protected override connected(): void {
    this.classList.add('gen-prompt-dialog');
    this.feed.acquire();
    this.onDisconnect(() => {
      this.feed.release();
      this.run++;
      this.releaseJob();
    });
    void this.loadRenderedPrompt();
  }

  protected template() {
    const phase = this.phase();
    const error = this.error();
    const showStream = this.showStream();
    return html`
      <h2 class="r2m-dialog__title">Generate voice prompt with AI</h2>
      <div class="r2m-dialog__content gen-prompt" data-phase=${phase}>
        ${error ? html`<p class="gen-prompt__error" role="alert">${error}</p>` : nothing}
        ${
          phase === 'rendering'
            ? html`<progress max="1" aria-label="Rendering prompt"></progress>
                <p class="gen-prompt__hint">
                  Rendering the prompt for ${this.data.characterName}…
                </p>`
            : html`<p class="gen-prompt__hint">
                  Review or edit the prompt before sending to the LLM.
                </p>
                <label class="r2m-field gen-prompt__field">
                  <span class="r2m-field__label">Prompt</span>
                  <textarea
                    aria-label="Prompt"
                    class="gen-prompt__textarea"
                    .value=${this.prompt()}
                    ?disabled=${phase === 'generating'}
                    @input=${(e: Event) => this.prompt.set((e.target as HTMLTextAreaElement).value)}
                  ></textarea>
                </label>
                ${
                  phase === 'generating'
                    ? html`<progress max="1" aria-label="Generating"></progress>`
                    : nothing
                }`
        }
        ${streamDisclosure({
          feed: this.feed,
          open: showStream,
          onToggle: () => this.showStream.set(!showStream),
        })}
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', null)}>
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="send"
          ?disabled=${!this.canSend()}
          @click=${() => void this.generate()}
        >
          ${icon('auto_awesome')} Send to AI
        </button>
      </div>
    `;
  }

  private async loadRenderedPrompt(): Promise<void> {
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

  async generate(): Promise<void> {
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
      const answer: GeneratePromptDialogResult = { designPrompt: result.designPrompt };
      this.emit('r2m-close', answer);
    } catch (e) {
      if (run !== this.run) return;
      this.error.set(toApiError(e).message);
      this.phase.set('edit');
    } finally {
      this.releaseJob();
    }
  }

  /** Once: closing the dialog and the call settling both land here. */
  private releaseJob(): void {
    const unregister = this.unregisterJob;
    this.unregisterJob = null;
    unregister?.();
  }
}
define('r2m-generate-prompt-dialog', GeneratePromptDialog);

/** Opens the dialog; resolves the generated design prompt, or null when cancelled. */
export async function openGeneratePromptDialog(
  data: GeneratePromptDialogData,
): Promise<GeneratePromptDialogResult | null> {
  const dialog = document.createElement('r2m-generate-prompt-dialog');
  dialog.data = data;
  return (await openDialog<GeneratePromptDialogResult | null>(dialog)) ?? null;
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-generate-prompt-dialog': GeneratePromptDialog;
  }
}
