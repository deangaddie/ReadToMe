import { TextFieldModule } from '@angular/cdk/text-field';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { MAX_VOICE_AUDIO_BYTES, VoiceDto, VoiceSource, VoicesApi, toApiError } from '@app/api';
import { Preflight } from '@app/shared/preflight';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { FileDrop, RejectedFile } from '@app/ui/file-drop/file-drop';
import { InlineEdit } from '@app/ui/inline-edit/inline-edit';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { CastStore } from '../cast-store';
import { openGeneratePromptDialog } from './generate-prompt-dialog';
import {
  canGenerateAudio,
  overwriteEditWarning,
  sourceSwitchWarning,
  voiceAudioUrl,
} from './voice-logic';
import { OverrideSave, VoiceOverrides } from './voice-overrides';

/**
 * One voice card (research §4 "Voices"): default star, inline rename, description, source chip,
 * Edited chip, Edit audio link, Delete; the Reference ⟷ Prompt toggle; the reference player and
 * upload, or the prompt editor with Regenerate with AI and Generate audio; the Advanced override
 * tabs; and the transcript with Send to AI. Drafts are per field and dirty-gate their Save, as
 * Blazor's VoiceDraftBuffer does; a reload never clobbers a draft.
 */
@Component({
  selector: 'app-voice-card',
  imports: [
    MatExpansionModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatTooltipModule,
    TextFieldModule,
    RouterLink,
    AudioPlayer,
    FileDrop,
    InlineEdit,
    StatusChip,
    VoiceOverrides,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'voice-card', '[attr.data-voice-id]': 'voice().id' },
  template: `
    <mat-expansion-panel class="voice-card__panel">
      <mat-expansion-panel-header class="voice-card__header">
        <mat-panel-title class="voice-card__title">
          @if (isDefault()) {
            <mat-icon class="voice-card__star voice-card__star--on" matTooltip="Default voice">
              star
            </mat-icon>
          } @else {
            <button
              mat-icon-button
              type="button"
              class="voice-card__star"
              data-action="set-default"
              matTooltip="Set as default"
              aria-label="Set as default voice"
              [disabled]="store.busy()"
              (click)="stop($event); setDefault()"
            >
              <mat-icon>star_border</mat-icon>
            </button>
          }
          <span
            class="voice-card__name"
            role="group"
            tabindex="-1"
            (click)="stop($event)"
            (keydown)="stop($event)"
          >
            <r2m-inline-edit
              [value]="voice().name"
              placeholder="Voice name"
              required
              (save)="rename($event)"
            />
            @if (voice().description) {
              <span class="voice-card__description">{{ voice().description }}</span>
            }
          </span>
        </mat-panel-title>
        <mat-panel-description class="voice-card__meta">
          <r2m-status-chip
            [status]="voice().source === 'Uploaded' ? 'info' : 'neutral'"
            [label]="voice().source === 'Uploaded' ? 'Reference' : 'Prompt'"
            compact
          />
          @if (voice().isEdited) {
            <r2m-status-chip
              status="warn"
              label="Edited"
              icon="tune"
              compact
              data-testid="voice-edited-chip"
            />
          }
          @if (voice().audioFileName) {
            <a
              mat-icon-button
              matTooltip="Edit audio"
              aria-label="Edit audio"
              data-action="edit-audio"
              [routerLink]="['/projects', folder(), 'voices', voice().id, 'editor']"
              (click)="stop($event)"
            >
              <mat-icon>graphic_eq</mat-icon>
            </a>
          }
          <button
            mat-icon-button
            type="button"
            class="voice-card__delete"
            matTooltip="Delete voice"
            aria-label="Delete voice"
            data-action="delete-voice"
            [disabled]="store.busy()"
            (click)="stop($event); remove()"
          >
            <mat-icon>delete</mat-icon>
          </button>
        </mat-panel-description>
      </mat-expansion-panel-header>

      <div class="voice-card__body">
        <section class="voice-card__block">
          <mat-form-field appearance="outline" class="voice-card__wide" subscriptSizing="dynamic">
            <mat-label>Voice description (where in the book this voice applies)</mat-label>
            <textarea
              matInput
              cdkTextareaAutosize
              cdkAutosizeMinRows="2"
              aria-label="Voice description"
              [value]="description()"
              (input)="descriptionDraft.set($any($event.target).value)"
            ></textarea>
          </mat-form-field>
          <button
            mat-stroked-button
            type="button"
            data-action="save-description"
            [disabled]="store.busy() || !descriptionDirty()"
            (click)="saveDescription()"
          >
            <mat-icon>save</mat-icon> Save description
          </button>
        </section>

        <mat-button-toggle-group
          class="voice-card__source"
          aria-label="Voice source"
          [value]="voice().source"
          [disabled]="store.busy()"
          (change)="switchSource($event.value)"
        >
          <mat-button-toggle value="Uploaded" data-source="Uploaded"
            >Reference audio</mat-button-toggle
          >
          <mat-button-toggle value="Generated" data-source="Generated">Prompt</mat-button-toggle>
        </mat-button-toggle-group>

        @if (voice().source === 'Uploaded') {
          <section class="voice-card__block" data-mode="reference">
            @if (audioUrl(); as src) {
              <r2m-audio-player [src]="src" [cacheKey]="audioVersion()" label="Reference audio" />
            } @else {
              <p class="voice-card__hint">No audio uploaded yet.</p>
            }
            @if (uploading()) {
              <p class="voice-card__hint" role="status">Uploading and normalising…</p>
            } @else {
              <r2m-file-drop
                accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a,.aac,.opus,.webm"
                [maxBytes]="maxAudioBytes"
                [label]="voice().audioFileName ? 'Replace audio' : 'Upload audio'"
                hint="Audio file (WAV, MP3, FLAC, OGG, M4A…), 200 MB max. The recording is normalised on upload."
                (files)="upload($event)"
                (rejected)="rejected($event)"
              />
            }
          </section>
        } @else {
          <section class="voice-card__block" data-mode="prompt">
            <mat-form-field appearance="outline" class="voice-card__wide" subscriptSizing="dynamic">
              <mat-label>Voice description prompt</mat-label>
              <textarea
                matInput
                cdkTextareaAutosize
                cdkAutosizeMinRows="4"
                aria-label="Voice description prompt"
                [value]="prompt()"
                (input)="promptDraft.set($any($event.target).value)"
              ></textarea>
            </mat-form-field>
            <div class="voice-card__actions">
              <button
                mat-stroked-button
                type="button"
                data-action="save-prompt"
                [disabled]="store.busy() || !promptDirty()"
                (click)="savePrompt()"
              >
                <mat-icon>save</mat-icon> Save prompt
              </button>
              <button
                mat-stroked-button
                type="button"
                data-action="regenerate-prompt"
                [disabled]="store.busy() || regeneratingPrompt()"
                (click)="regeneratePrompt()"
              >
                <mat-icon>auto_fix_high</mat-icon>
                {{ regeneratingPrompt() ? 'Generating…' : 'Regenerate with AI' }}
              </button>
              <button
                mat-stroked-button
                type="button"
                class="voice-card__generate"
                [class.voice-card__generate--primary]="!voice().audioFileName"
                data-action="generate-audio"
                [disabled]="store.busy() || generatingAudio() || !canGenerate()"
                (click)="generateAudio()"
              >
                <mat-icon>volume_up</mat-icon>
                {{
                  generatingAudio()
                    ? 'Generating…'
                    : voice().audioFileName
                      ? 'Regenerate audio'
                      : 'Generate audio'
                }}
              </button>
            </div>
            @if (audioUrl(); as src) {
              <r2m-audio-player [src]="src" [cacheKey]="audioVersion()" label="Generated audio" />
            }
          </section>
        }

        <mat-expansion-panel class="voice-card__sub" data-section="advanced">
          <mat-expansion-panel-header>
            <mat-panel-title>Advanced settings</mat-panel-title>
          </mat-expansion-panel-header>
          <ng-template matExpansionPanelContent>
            <app-voice-overrides
              [voice]="voice()"
              [disabled]="store.busy()"
              (save)="saveOverride($event)"
            />
          </ng-template>
        </mat-expansion-panel>

        <mat-expansion-panel class="voice-card__sub" data-section="transcript">
          <mat-expansion-panel-header>
            <mat-panel-title>Transcript</mat-panel-title>
          </mat-expansion-panel-header>
          <section class="voice-card__block">
            <mat-form-field appearance="outline" class="voice-card__wide" subscriptSizing="dynamic">
              <mat-label>Transcript</mat-label>
              <textarea
                matInput
                cdkTextareaAutosize
                cdkAutosizeMinRows="3"
                aria-label="Transcript"
                [value]="transcript()"
                (input)="transcriptDraft.set($any($event.target).value)"
              ></textarea>
            </mat-form-field>
            <div class="voice-card__actions">
              <button
                mat-stroked-button
                type="button"
                data-action="save-transcript"
                [disabled]="store.busy() || !transcriptDirty()"
                (click)="saveTranscript()"
              >
                <mat-icon>save</mat-icon> Save
              </button>
              @if (voice().audioFileName) {
                <button
                  mat-stroked-button
                  type="button"
                  data-action="transcribe"
                  [disabled]="store.busy() || transcribing()"
                  (click)="transcribe()"
                >
                  <mat-icon>record_voice_over</mat-icon>
                  {{ transcribing() ? 'Transcribing…' : 'Send to AI' }}
                </button>
              }
            </div>
          </section>
        </mat-expansion-panel>
      </div>
    </mat-expansion-panel>
  `,
  styles: `
    :host {
      display: block;
    }
    .voice-card__title {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      min-width: 0;
      flex: 1 1 auto;
    }
    .voice-card__star {
      --mdc-icon-button-state-layer-size: 32px;
      width: 32px;
      height: 32px;
      padding: 4px;
      flex: none;
    }
    .voice-card__star--on {
      color: var(--r2m-status-warn);
    }
    .voice-card__name {
      display: flex;
      flex-direction: column;
      min-width: 0;
      gap: 2px;
    }
    .voice-card__description {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .voice-card__meta {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: var(--r2m-space-1);
      flex: 0 0 auto;
      margin-right: 0;
    }
    .voice-card__delete {
      color: var(--r2m-status-error);
    }
    .voice-card__body {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding-top: var(--r2m-space-2);
    }
    .voice-card__block {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .voice-card__block > button {
      align-self: flex-start;
    }
    .voice-card__wide {
      width: 100%;
    }
    .voice-card__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-2);
    }
    .voice-card__source {
      align-self: flex-start;
    }
    .voice-card__generate--primary {
      color: var(--r2m-accent);
      border-color: var(--r2m-accent);
    }
    .voice-card__hint {
      margin: 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .voice-card__sub {
      box-shadow: none;
      border: 1px solid var(--r2m-outline);
    }
  `,
})
export class VoiceCard {
  protected readonly store = inject(CastStore);
  private readonly voices = inject(VoicesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly dialog = inject(MatDialog);
  private readonly preflight = inject(Preflight);
  private readonly toast = inject(ToastService);

  readonly voice = input.required<VoiceDto>();
  readonly characterName = input.required<string>();
  readonly isDefault = input(false);

  protected readonly maxAudioBytes = MAX_VOICE_AUDIO_BYTES;
  protected readonly folder = computed(() => this.store.folder() ?? '');
  protected readonly audioVersion = computed(
    () => this.store.audioVersions()[this.voice().id] ?? 0,
  );
  protected readonly audioUrl = computed(() =>
    voiceAudioUrl(this.folder(), this.voice(), this.store.audioVersions()),
  );

  // Drafts: null = untouched (shows the stored value); a string = what the user typed.
  protected readonly descriptionDraft = signal<string | null>(null);
  protected readonly promptDraft = signal<string | null>(null);
  protected readonly transcriptDraft = signal<string | null>(null);

  protected readonly description = computed(
    () => this.descriptionDraft() ?? this.voice().description ?? '',
  );
  protected readonly prompt = computed(() => this.promptDraft() ?? this.voice().designPrompt ?? '');
  protected readonly transcript = computed(
    () => this.transcriptDraft() ?? this.voice().transcript ?? '',
  );
  protected readonly descriptionDirty = computed(
    () =>
      this.descriptionDraft() !== null && this.description() !== (this.voice().description ?? ''),
  );
  protected readonly promptDirty = computed(
    () => this.promptDraft() !== null && this.prompt() !== (this.voice().designPrompt ?? ''),
  );
  protected readonly transcriptDirty = computed(
    () => this.transcriptDraft() !== null && this.transcript() !== (this.voice().transcript ?? ''),
  );
  protected readonly canGenerate = computed(() => canGenerateAudio(this.prompt()));

  protected readonly uploading = signal(false);
  protected readonly regeneratingPrompt = signal(false);
  protected readonly generatingAudio = signal(false);
  protected readonly transcribing = signal(false);

  constructor() {
    // A different voice in this slot (the list re-sorted) starts with clean drafts.
    effect(() => {
      const id = this.voice().id;
      untracked(() => this.resetDrafts(id));
    });
  }

  /** Drafts belong to one voice; a different voice in this slot starts clean. */
  private resetDrafts(voiceId: string): void {
    if (this.draftsFor === voiceId) return;
    this.draftsFor = voiceId;
    this.descriptionDraft.set(null);
    this.promptDraft.set(null);
    this.transcriptDraft.set(null);
  }

  private draftsFor: string | null = null;

  /** Clicks and keys inside the header controls must not toggle the panel (Space/Enter do on the header). */
  protected stop(event: Event): void {
    event.stopPropagation();
  }

  protected async setDefault(): Promise<void> {
    await this.store.run({ type: 'SetVoiceDefault', voiceId: this.voice().id });
  }

  protected async rename(name: string): Promise<void> {
    const voice = this.voice();
    if (name === voice.name) return;
    await this.store.run({
      type: 'UpdateVoice',
      voiceId: voice.id,
      name,
      description: voice.description,
    });
  }

  protected async saveDescription(): Promise<void> {
    const voice = this.voice();
    const description = this.description().trim() || null;
    const response = await this.store.run({
      type: 'UpdateVoice',
      voiceId: voice.id,
      name: voice.name,
      description,
    });
    if (response) this.descriptionDraft.set(null);
  }

  protected async remove(): Promise<void> {
    const voice = this.voice();
    const ok = await this.confirm.confirm({
      title: 'Delete voice',
      message: `Delete the voice "${voice.name}"? Its audio and any rules pointing at it are removed.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!ok) return;
    await this.store.run({ type: 'DeleteVoice', voiceId: voice.id });
  }

  protected async switchSource(target: VoiceSource): Promise<void> {
    const voice = this.voice();
    if (target === voice.source) return;
    const warning = sourceSwitchWarning(voice, target);
    if (warning) {
      const ok = await this.confirm.confirm({
        title: target === 'Generated' ? 'Switch to prompt' : 'Switch to reference audio',
        message: warning,
        confirmLabel: 'Switch',
        destructive: true,
      });
      if (!ok) return;
    }
    await this.store.run({
      type: 'SetVoiceSource',
      voiceId: voice.id,
      isGenerated: target === 'Generated',
    });
  }

  // ---- reference audio ---------------------------------------------------------------------------

  protected async upload(files: File[]): Promise<void> {
    const file = files[0];
    if (!file) return;
    const voice = this.voice();
    if (!(await this.confirmOverwriteEdit(voice))) return;
    this.uploading.set(true);
    try {
      await this.voices.uploadAudio(this.folder(), voice.id, file);
      this.store.bumpAudio(voice.id);
      this.toast.success('Voice audio normalised.');
      await this.store.refresh();
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.uploading.set(false);
    }
  }

  protected rejected(files: RejectedFile[]): void {
    const first = files[0];
    if (!first) return;
    this.toast.warn(
      first.reason === 'size'
        ? 'Voice audio must be 200 MB or smaller.'
        : 'Choose an audio file (WAV, MP3, FLAC, OGG, M4A…).',
    );
  }

  // ---- prompt --------------------------------------------------------------------------------------

  protected async savePrompt(): Promise<void> {
    const response = await this.store.run({
      type: 'SetVoiceDesignPrompt',
      voiceId: this.voice().id,
      prompt: this.prompt(),
    });
    if (response) this.promptDraft.set(null);
  }

  protected async regeneratePrompt(): Promise<void> {
    if (!(await this.preflight.ensureReady('voicePrompt'))) return;
    this.regeneratingPrompt.set(true);
    try {
      const result = await openGeneratePromptDialog(this.dialog, {
        folder: this.folder(),
        characterId: this.voice().characterId,
        characterName: this.characterName(),
      });
      // The answer is a draft: Save prompt persists it, as in Blazor.
      if (result) this.promptDraft.set(result.designPrompt);
    } finally {
      this.regeneratingPrompt.set(false);
    }
  }

  protected async generateAudio(): Promise<void> {
    const voice = this.voice();
    if (!this.canGenerate()) return;
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
      await this.voices.generateAudio(this.folder(), voice.characterId, voice.id);
      this.store.bumpAudio(voice.id);
      await this.store.refresh();
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.generatingAudio.set(false);
    }
  }

  // ---- overrides -----------------------------------------------------------------------------------

  protected async saveOverride(save: OverrideSave): Promise<void> {
    const voiceId = this.voice().id;
    await this.store.run(
      save.area === 'paragraph-tts'
        ? { type: 'SetVoiceTtsSettingsOverride', voiceId, json: save.json }
        : { type: 'SetVoiceSettingsOverride', voiceId, json: save.json },
    );
  }

  // ---- transcript ----------------------------------------------------------------------------------

  protected async saveTranscript(): Promise<void> {
    const response = await this.store.run({
      type: 'SetVoiceTranscript',
      voiceId: this.voice().id,
      transcript: this.transcript(),
    });
    if (response) this.transcriptDraft.set(null);
  }

  protected async transcribe(): Promise<void> {
    const voice = this.voice();
    if (!voice.audioFileName) return;
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
