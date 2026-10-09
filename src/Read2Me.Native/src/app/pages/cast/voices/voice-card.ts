import { html, nothing } from 'lit-html';
import { live } from 'lit-html/directives/live.js';
import {
  MAX_VOICE_AUDIO_BYTES,
  type VoiceDto,
  type VoiceSource,
  VoicesApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { type ReadonlySignal, computed, signal, untracked } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/dialogs';
import type { RejectedFile } from '@app/ui/file-drop';
import { icon, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { CastStore } from '../cast-store';
import { openGeneratePromptDialog } from './generate-prompt-dialog';
import {
  canGenerateAudio,
  overwriteEditWarning,
  sourceSwitchWarning,
  voiceAudioUrl,
} from './voice-logic';
import { type OverrideSave, type OverrideTab, voiceOverrides } from './voice-overrides';
import '@app/ui/audio-player';
import '@app/ui/file-drop';
import '@app/ui/inline-edit';

const valueOf = (event: Event): string => (event.target as HTMLTextAreaElement).value;

/**
 * One voice card (research §4 "Voices"): default star, inline rename, description, source chip,
 * Edited chip, Edit audio link, Delete; the Reference ⟷ Prompt toggle; the reference player and
 * upload, or the prompt editor with Regenerate with AI and Generate audio; the Advanced override
 * tabs; and the transcript with Send to AI. Drafts are per field and dirty-gate their Save;
 * a reload never clobbers a draft. The card and its two sub-sections are `<details>` panels.
 */
export class VoiceCard extends R2mElement {
  private readonly voices = use(VoicesApi);
  private readonly confirm = use(ConfirmService);
  private readonly preflight = use(Preflight);
  private readonly toast = use(ToastService);
  private store!: CastStore;

  readonly #voice = signal<VoiceDto | null>(null);
  get voice() {
    return this.#voice();
  }
  set voice(value: VoiceDto | null) {
    this.#voice.set(value);
  }

  readonly #characterName = signal('');
  get characterName() {
    return this.#characterName();
  }
  set characterName(value: string) {
    this.#characterName.set(value);
  }

  readonly #isDefault = signal(false);
  get isDefault() {
    return this.#isDefault();
  }
  set isDefault(value: boolean) {
    this.#isDefault.set(value);
  }

  // Drafts: null = untouched (shows the stored value); a string = what the user typed.
  readonly descriptionDraft = signal<string | null>(null);
  readonly promptDraft = signal<string | null>(null);
  readonly transcriptDraft = signal<string | null>(null);

  readonly description = computed(
    () => this.descriptionDraft() ?? this.#voice()?.description ?? '',
  );
  readonly prompt = computed(() => this.promptDraft() ?? this.#voice()?.designPrompt ?? '');
  readonly transcript = computed(() => this.transcriptDraft() ?? this.#voice()?.transcript ?? '');
  readonly descriptionDirty = computed(
    () =>
      this.descriptionDraft() !== null && this.description() !== (this.#voice()?.description ?? ''),
  );
  readonly promptDirty = computed(
    () => this.promptDraft() !== null && this.prompt() !== (this.#voice()?.designPrompt ?? ''),
  );
  readonly transcriptDirty = computed(
    () =>
      this.transcriptDraft() !== null && this.transcript() !== (this.#voice()?.transcript ?? ''),
  );
  readonly canGenerate = computed(() => canGenerateAudio(this.prompt()));

  readonly uploading = signal(false);
  readonly regeneratingPrompt = signal(false);
  readonly generatingAudio = signal(false);
  readonly transcribing = signal(false);
  /** Which Advanced tab is open. */
  readonly overrideTab = signal<OverrideTab>('voice-design');

  /** Built in `connected()`, once the page's store is reachable through the DOM. */
  private folder!: ReadonlySignal<string>;
  private audioVersion!: ReadonlySignal<number>;
  private audioUrl!: ReadonlySignal<string | null>;
  private draftsFor: string | null = null;

  protected override connected(): void {
    this.classList.add('voice-card');
    this.store = use(CastStore, this);
    this.folder = computed(() => this.store.folder() ?? '');
    this.audioVersion = computed(() => {
      const id = this.#voice()?.id;
      return id ? (this.store.audioVersions()[id] ?? 0) : 0;
    });
    this.audioUrl = computed(() => {
      const voice = this.#voice();
      return voice ? voiceAudioUrl(this.folder(), voice, this.store.audioVersions()) : null;
    });
    // A different voice in this slot (the list re-sorted) starts with clean drafts.
    this.effect(() => {
      const id = this.#voice()?.id ?? null;
      untracked(() => this.resetDrafts(id));
    });
  }

  /** Drafts belong to one voice; a different voice in this slot starts clean. */
  private resetDrafts(voiceId: string | null): void {
    if (this.draftsFor === voiceId) return;
    this.draftsFor = voiceId;
    this.descriptionDraft.set(null);
    this.promptDraft.set(null);
    this.transcriptDraft.set(null);
  }

  protected template() {
    const voice = this.#voice();
    if (!voice) return nothing;
    this.setAttribute('data-voice-id', voice.id);
    const busy = this.store.busy();
    // A write is in flight: every Save is off until the reload lands.
    if (busy) this.setAttribute('aria-busy', 'true');
    else this.removeAttribute('aria-busy');
    return html`
      <details class="r2m-expansion voice-card__panel">
        <summary class="voice-card__header">
          <span class="voice-card__title">
            ${
              this.#isDefault()
                ? html`<span
                    class="voice-card__star voice-card__star--on"
                    data-tooltip="Default voice"
                  >
                    ${icon('star', 'r2m-icon--filled')}
                  </span>`
                : html`<button
                    type="button"
                    class="r2m-icon-button voice-card__star"
                    data-action="set-default"
                    data-tooltip="Set as default"
                    aria-label="Set as default voice"
                    ?disabled=${busy}
                    @click=${(e: Event) => {
                      this.stop(e);
                      void this.setDefault();
                    }}
                  >
                    ${icon('star')}
                  </button>`
            }
            <span
              class="voice-card__name"
              role="group"
              tabindex="-1"
              @click=${this.stop}
              @keydown=${this.stop}
            >
              <r2m-inline-edit
                .value=${voice.name}
                .placeholder=${'Voice name'}
                .required=${true}
                @save=${(e: Event) => void this.rename((e as CustomEvent<string>).detail)}
              ></r2m-inline-edit>
              ${
                voice.description
                  ? html`<span class="voice-card__description">${voice.description}</span>`
                  : nothing
              }
            </span>
          </span>
          <span class="voice-card__meta" @click=${this.stop} @keydown=${this.stop}>
            ${statusChip({
              status: voice.source === 'Uploaded' ? 'info' : 'neutral',
              label: voice.source === 'Uploaded' ? 'Reference' : 'Prompt',
              compact: true,
            })}
            ${
              voice.isEdited
                ? html`<span data-testid="voice-edited-chip"
                    >${statusChip({ status: 'warn', label: 'Edited', icon: 'tune', compact: true })}</span
                  >`
                : nothing
            }
            ${
              voice.referenceWarning
                ? html`<span data-testid="voice-reference-warning"
                    >${statusChip({
                      status: 'warn',
                      label: `${voice.referenceSeconds} s`,
                      icon: 'timer',
                      compact: true,
                      tooltip: voice.referenceWarning,
                    })}</span
                  >`
                : nothing
            }
            ${
              voice.audioFileName
                ? html`<a
                    class="r2m-icon-button"
                    data-tooltip="Edit audio"
                    aria-label="Edit audio"
                    data-action="edit-audio"
                    href=${`projects/${encodeURIComponent(this.folder())}/voices/${voice.id}/editor`}
                  >
                    ${icon('graphic_eq')}
                  </a>`
                : nothing
            }
            <button
              type="button"
              class="r2m-icon-button voice-card__delete"
              data-tooltip="Delete voice"
              aria-label="Delete voice"
              data-action="delete-voice"
              ?disabled=${busy}
              @click=${() => void this.deleteVoice()}
            >
              ${icon('delete')}
            </button>
          </span>
        </summary>

        <div class="r2m-expansion__body voice-card__body">
          <section class="voice-card__block">
            <label class="r2m-field voice-card__wide">
              <span class="r2m-field__label"
                >Voice description (where in the book this voice applies)</span
              >
              <textarea
                aria-label="Voice description"
                rows="2"
                .value=${this.description()}
                @input=${(e: Event) => this.descriptionDraft.set(valueOf(e))}
              ></textarea>
            </label>
            <button
              type="button"
              class="r2m-button r2m-button--stroked"
              data-action="save-description"
              ?disabled=${busy || !this.descriptionDirty()}
              @click=${() => void this.saveDescription()}
            >
              ${icon('save')} Save description
            </button>
          </section>

          <fieldset
            class="r2m-segmented voice-card__source"
            role="radiogroup"
            aria-label="Voice source"
          >
            <label data-source="Uploaded">
              <input
                type="radio"
                name=${`source-${voice.id}`}
                value="Uploaded"
                .checked=${live(voice.source === 'Uploaded')}
                ?disabled=${busy}
                @change=${() => void this.switchSource('Uploaded')}
              />
              Reference audio
            </label>
            <label data-source="Generated">
              <input
                type="radio"
                name=${`source-${voice.id}`}
                value="Generated"
                .checked=${live(voice.source === 'Generated')}
                ?disabled=${busy}
                @change=${() => void this.switchSource('Generated')}
              />
              Prompt
            </label>
          </fieldset>

          ${voice.source === 'Uploaded' ? this.reference(voice) : this.promptSection(voice, busy)}

          <details class="r2m-expansion voice-card__sub" data-section="advanced">
            <summary><span class="r2m-expansion__title">Advanced settings</span></summary>
            <div class="r2m-expansion__body">
              ${voiceOverrides({
                voice,
                disabled: busy,
                selected: this.overrideTab(),
                onSelect: (tab) => this.overrideTab.set(tab),
                onSave: (save) => void this.saveOverride(save),
              })}
            </div>
          </details>

          <details class="r2m-expansion voice-card__sub" data-section="transcript">
            <summary><span class="r2m-expansion__title">Transcript</span></summary>
            <section class="r2m-expansion__body voice-card__block">
              <label class="r2m-field voice-card__wide">
                <span class="r2m-field__label">Transcript</span>
                <textarea
                  aria-label="Transcript"
                  rows="3"
                  .value=${this.transcript()}
                  @input=${(e: Event) => this.transcriptDraft.set(valueOf(e))}
                ></textarea>
              </label>
              <div class="voice-card__actions">
                <button
                  type="button"
                  class="r2m-button r2m-button--stroked"
                  data-action="save-transcript"
                  ?disabled=${busy || !this.transcriptDirty()}
                  @click=${() => void this.saveTranscript()}
                >
                  ${icon('save')} Save
                </button>
                ${
                  voice.audioFileName
                    ? html`<button
                        type="button"
                        class="r2m-button r2m-button--stroked"
                        data-action="transcribe"
                        ?disabled=${busy || this.transcribing()}
                        @click=${() => void this.transcribe()}
                      >
                        ${icon('record_voice_over')}
                        ${this.transcribing() ? 'Transcribing…' : 'Send to AI'}
                      </button>`
                    : nothing
                }
              </div>
            </section>
          </details>
        </div>
      </details>
    `;
  }

  private reference(voice: VoiceDto) {
    const src = this.audioUrl();
    return html`<section class="voice-card__block" data-mode="reference">
      ${
        src
          ? html`<r2m-audio-player
              .src=${src}
              .cacheKey=${this.audioVersion()}
              .label=${'Reference audio'}
            ></r2m-audio-player>`
          : html`<p class="voice-card__hint">No audio uploaded yet.</p>`
      }
      ${
        this.uploading()
          ? html`<p class="voice-card__hint" role="status">Uploading and normalising…</p>`
          : html`<r2m-file-drop
              .accept=${'audio/*,.wav,.mp3,.flac,.ogg,.m4a,.aac,.opus,.webm'}
              .maxBytes=${MAX_VOICE_AUDIO_BYTES}
              .label=${voice.audioFileName ? 'Replace audio' : 'Upload audio'}
              .hint=${'Audio file (WAV, MP3, FLAC, OGG, M4A…), 200 MB max. The reference must be 30 s or shorter; 15 s or shorter clones best. The recording is normalised on upload.'}
              @files=${(e: Event) => void this.upload((e as CustomEvent<File[]>).detail)}
              @rejected=${(e: Event) => this.rejected((e as CustomEvent<RejectedFile[]>).detail)}
            ></r2m-file-drop>`
      }
    </section>`;
  }

  private promptSection(voice: VoiceDto, busy: boolean) {
    const src = this.audioUrl();
    const generating = this.generatingAudio();
    return html`<section class="voice-card__block" data-mode="prompt">
      <label class="r2m-field voice-card__wide">
        <span class="r2m-field__label">Voice description prompt</span>
        <textarea
          aria-label="Voice description prompt"
          rows="4"
          .value=${this.prompt()}
          @input=${(e: Event) => this.promptDraft.set(valueOf(e))}
        ></textarea>
      </label>
      <div class="voice-card__actions">
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="save-prompt"
          ?disabled=${busy || !this.promptDirty()}
          @click=${() => void this.savePrompt()}
        >
          ${icon('save')} Save prompt
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="regenerate-prompt"
          ?disabled=${busy || this.regeneratingPrompt()}
          @click=${() => void this.regeneratePrompt()}
        >
          ${icon('auto_fix_high')}
          ${this.regeneratingPrompt() ? 'Generating…' : 'Regenerate with AI'}
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--stroked voice-card__generate ${
            voice.audioFileName ? '' : 'voice-card__generate--primary'
          }"
          data-action="generate-audio"
          ?disabled=${busy || generating || !this.canGenerate()}
          @click=${() => void this.generateAudio()}
        >
          ${icon('volume_up')}
          ${generating ? 'Generating…' : voice.audioFileName ? 'Regenerate audio' : 'Generate audio'}
        </button>
      </div>
      ${
        src
          ? html`<r2m-audio-player
              .src=${src}
              .cacheKey=${this.audioVersion()}
              .label=${'Generated audio'}
            ></r2m-audio-player>`
          : nothing
      }
    </section>`;
  }

  /** Clicks and keys inside the header controls must not toggle the panel (Space/Enter do on the summary). */
  private readonly stop = (event: Event): void => {
    event.stopPropagation();
    // A click on a control inside <summary> would also toggle the panel.
    if (event.type === 'click') event.preventDefault();
  };

  async setDefault(): Promise<void> {
    const voice = this.#voice();
    if (voice) await this.store.run({ type: 'SetVoiceDefault', voiceId: voice.id });
  }

  async rename(name: string): Promise<void> {
    const voice = this.#voice();
    if (!voice || name === voice.name) return;
    await this.store.run({
      type: 'UpdateVoice',
      voiceId: voice.id,
      name,
      description: voice.description,
    });
  }

  async saveDescription(): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    const description = this.description().trim() || null;
    const response = await this.store.run({
      type: 'UpdateVoice',
      voiceId: voice.id,
      name: voice.name,
      description,
    });
    if (response) this.descriptionDraft.set(null);
  }

  async deleteVoice(): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    const ok = await this.confirm.confirm({
      title: 'Delete voice',
      message: `Delete the voice "${voice.name}"? Its audio and any rules pointing at it are removed.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!ok) return;
    await this.store.run({ type: 'DeleteVoice', voiceId: voice.id });
  }

  async switchSource(target: VoiceSource): Promise<void> {
    const voice = this.#voice();
    if (!voice || target === voice.source) return;
    const warning = sourceSwitchWarning(voice, target);
    if (warning) {
      const ok = await this.confirm.confirm({
        title: target === 'Generated' ? 'Switch to prompt' : 'Switch to reference audio',
        message: warning,
        confirmLabel: 'Switch',
        destructive: true,
      });
      if (!ok) {
        // The radio moved on its own; put it back on the stored source.
        this.#voice.set({ ...voice });
        return;
      }
    }
    await this.store.run({
      type: 'SetVoiceSource',
      voiceId: voice.id,
      isGenerated: target === 'Generated',
    });
  }

  // ---- reference audio ---------------------------------------------------------------------------

  async upload(files: File[]): Promise<void> {
    const file = files[0];
    const voice = this.#voice();
    if (!file || !voice) return;
    if (!(await this.confirmOverwriteEdit(voice))) return;
    this.uploading.set(true);
    try {
      const stored = await this.voices.uploadAudio(this.folder(), voice.id, file);
      this.store.bumpAudio(voice.id);
      // Over the soft Reference Limit it is kept, but the upload says so as well as the badge.
      if (stored.referenceWarning) this.toast.warn(stored.referenceWarning);
      else this.toast.success('Voice audio normalised.');
      await this.store.refresh();
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.uploading.set(false);
    }
  }

  rejected(files: RejectedFile[]): void {
    const first = files[0];
    if (!first) return;
    this.toast.warn(
      first.reason === 'size'
        ? 'Voice audio must be 200 MB or smaller.'
        : 'Choose an audio file (WAV, MP3, FLAC, OGG, M4A…).',
    );
  }

  // ---- prompt --------------------------------------------------------------------------------------

  async savePrompt(): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    const response = await this.store.run({
      type: 'SetVoiceDesignPrompt',
      voiceId: voice.id,
      prompt: this.prompt(),
    });
    if (response) this.promptDraft.set(null);
  }

  async regeneratePrompt(): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    if (!(await this.preflight.ensureReady('voicePrompt'))) return;
    this.regeneratingPrompt.set(true);
    try {
      const result = await openGeneratePromptDialog({
        folder: this.folder(),
        characterId: voice.characterId,
        characterName: this.#characterName(),
      });
      // The answer is a draft: only Save prompt persists it.
      if (result) this.promptDraft.set(result.designPrompt);
    } finally {
      this.regeneratingPrompt.set(false);
    }
  }

  async generateAudio(): Promise<void> {
    const voice = this.#voice();
    if (!voice || !this.canGenerate()) return;
    if (!(await this.confirmOverwriteEdit(voice))) return;
    if (!(await this.preflight.ensureReady('voiceDesign'))) return;
    this.generatingAudio.set(true);
    try {
      // The host synthesises from the stored prompt: a dirty draft is saved first.
      if (this.promptDirty()) {
        const saved = await this.store.run({
          type: 'SetVoiceDesignPrompt',
          voiceId: voice.id,
          prompt: this.prompt(),
        });
        if (!saved) return;
        this.promptDraft.set(null);
      }
      const generated = await this.voices.generateAudio(this.folder(), voice.characterId, voice.id);
      this.store.bumpAudio(voice.id);
      // Over the soft Reference Limit the take is kept, but generating says so, as an upload does.
      if (generated.referenceWarning) this.toast.warn(generated.referenceWarning);
      await this.store.refresh();
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.generatingAudio.set(false);
    }
  }

  // ---- overrides -----------------------------------------------------------------------------------

  async saveOverride(save: OverrideSave): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    const voiceId = voice.id;
    await this.store.run(
      save.area === 'paragraph-tts'
        ? { type: 'SetVoiceTtsSettingsOverride', voiceId, json: save.json }
        : { type: 'SetVoiceSettingsOverride', voiceId, json: save.json },
    );
  }

  // ---- transcript ----------------------------------------------------------------------------------

  async saveTranscript(): Promise<void> {
    const voice = this.#voice();
    if (!voice) return;
    const response = await this.store.run({
      type: 'SetVoiceTranscript',
      voiceId: voice.id,
      transcript: this.transcript(),
    });
    if (response) this.transcriptDraft.set(null);
  }

  async transcribe(): Promise<void> {
    const voice = this.#voice();
    if (!voice?.audioFileName) return;
    if (!(await this.preflight.ensureReady('transcription'))) return;
    this.transcribing.set(true);
    try {
      await this.voices.transcribe(this.folder(), voice.id);
      this.transcriptDraft.set(null);
      this.toast.success('Transcript generated.');
      await this.store.refreshVoices();
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.transcribing.set(false);
    }
  }

  /** Fresh audio discards an edit — allowed, but never silently. No edit, no dialog. */
  private async confirmOverwriteEdit(voice: VoiceDto): Promise<boolean> {
    const warning = overwriteEditWarning(voice);
    if (!warning) return true;
    return this.confirm.confirm({
      title: 'Replace edited audio',
      message: warning,
      confirmLabel: 'Replace',
      destructive: true,
    });
  }
}
define('r2m-voice-card', VoiceCard);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-card': VoiceCard;
  }
}
