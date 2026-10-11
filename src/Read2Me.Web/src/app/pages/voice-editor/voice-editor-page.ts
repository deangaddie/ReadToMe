import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import {
  type PreviewResponse,
  type PreviewStepRequest,
  type StepCatalogEntryDto,
  VoiceEditorApi,
  type VoiceDto,
  VoicesApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import {
  type ReadonlySignal,
  type WritableSignal,
  computed,
  signal,
  untracked,
} from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService } from '@app/ui/dialogs';
import { emptyState, icon, spinner, statusChip } from '@app/ui/partials';
import type { SettingsValues } from '@app/ui/settings-form';
import { castPath } from '../cast/cast-path';
import { ProjectStore } from '../project/project-store';
import {
  type StepValues,
  type Ticks,
  applyBlockedReason,
  buildPreviewSteps,
  hissRedundant,
  originalAudioUrl,
  sameRender,
  seedValues,
  stageFor,
  toStepSchema,
} from './editor-logic';
import '@app/ui/audio-player';
import '@app/ui/settings-form';
import voiceEditorCss from './voice-editor-page.css' with { type: 'text' };

adoptStyles(voiceEditorCss);

/** The last render: what was asked for and what came back. */
interface Render {
  request: PreviewStepRequest[];
  response: PreviewResponse;
}

/**
 * `/projects/{folder}/voices/{voiceId}/editor` (ticket 18, design §6.4): the step checklist on the
 * left with the selected step's dials on the right, an Original player plus one "After <step>"
 * player per rendered stage, and Apply gated on a render that matches the current ticks and dials.
 * The host re-checks that gate through `previewId`; this page only explains it. Reads the folder
 * from the project shell's {@link ProjectStore} and the voice id from the route.
 */
export class VoiceEditorPage extends R2mElement {
  private readonly voices = use(VoicesApi);
  private readonly editor = use(VoiceEditorApi);
  private readonly confirm = use(ConfirmService);
  private readonly router = use(Router);
  private project!: ProjectStore;

  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  /** The last apply/restore outcome, shown inline (design §8: own-action outcomes are not toasts). */
  readonly actionNote = signal<string | null>(null);
  readonly voice = signal<VoiceDto | null>(null);
  readonly catalog = signal<StepCatalogEntryDto[]>([]);

  readonly ticks = signal<Ticks>({});
  readonly values = signal<StepValues>({});
  readonly selectedStepId = signal<string | null>(null);
  readonly lastRender = signal<Render | null>(null);
  readonly rendering = signal(false);
  readonly writing = signal(false);
  /** Bumped whenever the voice's files change under the same names, so the players refetch. */
  readonly audioVersion = signal(0);

  readonly busy = computed(() => this.rendering() || this.writing());
  readonly anyTicked = computed(() => Object.values(this.ticks()).some(Boolean));
  readonly currentRequest = computed(() =>
    buildPreviewSteps(this.catalog(), this.ticks(), this.values()),
  );
  readonly stale = computed(() => {
    const last = this.lastRender();
    return last !== null && !sameRender(last.request, this.currentRequest());
  });
  readonly applyBlocked = computed(() =>
    applyBlockedReason({
      anyTicked: this.anyTicked(),
      hasRender: this.lastRender() !== null,
      stale: this.stale(),
      busy: this.busy(),
    }),
  );
  readonly hissRedundant = computed(() => hissRedundant(this.ticks()));
  readonly stages = computed(() => this.lastRender()?.response.stages ?? []);
  readonly selected = computed(
    () => this.catalog().find((e) => e.stepId === this.selectedStepId()) ?? null,
  );

  /** Route param. */
  private readonly voiceId = computed(() => this.router.params()['voiceId'] ?? '');
  /** Built in `connected()`, once the shell's store is reachable through the DOM. */
  private folder!: ReadonlySignal<string>;

  protected override connected(): void {
    this.classList.add('voice-editor');
    this.project = use(ProjectStore, this);
    this.folder = computed(() => this.project.folder() ?? '');

    this.effect(() => {
      const folder = this.folder();
      const voiceId = this.voiceId();
      if (!folder || !voiceId) return;
      untracked(() => void this.load(folder, voiceId));
    });
  }

  protected template() {
    const voice = this.voice();
    const loadError = this.loadError();
    const backHref = castPath(this.folder(), voice?.characterId);
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Edit voice audio</h1>
        ${voice ? html`<p class="r2m-page-header__subtitle">${voice.name}</p>` : nothing}
        <a class="r2m-button voice-editor__back" data-action="back" href=${backHref}>
          ${icon('arrow_back')} Back
        </a>
      </header>

      ${
        this.loading()
          ? html`<progress max="1" aria-label="Loading voice"></progress>`
          : loadError !== null
            ? emptyState(
                { icon: 'voice_over_off', headline: 'Cannot edit this voice', hint: loadError },
                html`<a class="r2m-button" data-action="back-empty" href=${backHref}
                  >Back to the cast</a
                >`,
              )
            : voice
              ? this.editorTemplate(voice)
              : nothing
      }
    `;
  }

  private editorTemplate(voice: VoiceDto) {
    const busy = this.busy();
    const applyBlocked = this.applyBlocked();
    const actionError = this.actionError();
    const actionNote = this.actionNote();
    return html`
      <div class="voice-editor__toolbar">
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="preview"
          ?disabled=${!this.anyTicked() || busy}
          @click=${() => void this.preview()}
        >
          ${this.rendering() ? spinner(18) : icon('play_circle')} Preview
        </button>
        <span data-tooltip=${applyBlocked ?? nothing}>
          <button
            type="button"
            class="r2m-button r2m-button--stroked"
            data-action="apply"
            ?disabled=${applyBlocked !== null}
            @click=${() => void this.apply()}
          >
            ${icon('check')} Apply
          </button>
        </span>
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
          voice.isEdited
            ? html`<span data-testid="edited-chip"
                  >${statusChip({ status: 'warn', label: 'Edited', icon: 'tune', compact: true })}</span
                >
                <button
                  type="button"
                  class="r2m-button voice-editor__danger"
                  data-action="restore"
                  ?disabled=${busy}
                  @click=${() => void this.restore()}
                >
                  ${icon('history')} Restore original
                </button>`
            : nothing
        }
        ${
          actionError !== null
            ? html`<span data-testid="action-error"
                >${statusChip({ status: 'error', label: actionError })}</span
              >`
            : actionNote !== null
              ? html`<span data-testid="action-note"
                  >${statusChip({ status: 'ok', icon: 'check', compact: true, label: actionNote })}</span
                >`
              : nothing
        }
      </div>

      <div class="voice-editor__body">
        <section class="voice-editor__steps" aria-label="Steps">
          ${repeat(
            this.catalog(),
            (entry) => entry.stepId,
            (entry) => this.stepTemplate(entry, busy),
          )}
        </section>

        <section class="voice-editor__dials" aria-label="Dials">
          ${this.dialsTemplate(busy)}
        </section>
      </div>

      <section class="voice-editor__players" aria-label="Players">
        <span data-testid="original-player">
          <r2m-audio-player
            .src=${originalAudioUrl(this.folder(), voice)}
            .cacheKey=${this.audioVersion()}
            .label=${voice.isEdited ? 'Original (before edits)' : 'Original'}
          ></r2m-audio-player>
        </span>
        ${repeat(
          this.stages(),
          (stage) => stage.stepId,
          (stage) =>
            html`<r2m-audio-player
              data-stage=${stage.stepId}
              .src=${stage.url}
              .label=${`After ${this.labelFor(stage.stepId)}`}
            ></r2m-audio-player>`,
        )}
        ${
          this.stale()
            ? html`<p class="voice-editor__hint" data-testid="stale-hint">
                Ticks or dials changed since this render — preview again before applying.
              </p>`
            : nothing
        }
      </section>
    `;
  }

  private stepTemplate(entry: StepCatalogEntryDto, busy: boolean) {
    const stage = stageFor(this.stages(), entry.stepId);
    const select = () => this.selectedStepId.set(entry.stepId);
    return html`<div
      class="voice-editor__step ${entry.stepId === this.selectedStepId() ? 'voice-editor__step--selected' : ''}"
      data-step=${entry.stepId}
      role="button"
      tabindex="0"
      @click=${select}
      @keydown=${(e: KeyboardEvent) => {
        if (e.key === 'Enter') select();
      }}
    >
      <label class="voice-editor__tick" @click=${(e: Event) => e.stopPropagation()}>
        <input
          type="checkbox"
          class="r2m-checkbox"
          data-tick=${entry.stepId}
          .checked=${!!this.ticks()[entry.stepId]}
          ?disabled=${busy}
          @change=${(e: Event) => this.tick(entry.stepId, (e.target as HTMLInputElement).checked)}
        />
        ${entry.label}
      </label>
      <p class="voice-editor__blurb">${entry.blurb}</p>
      ${
        stage && !stage.applied
          ? html`<span data-testid="skipped-chip"
              >${statusChip({
                status: 'warn',
                icon: 'skip_next',
                compact: true,
                label: `Skipped — ${stage.reason ?? 'no reason given'}`,
              })}</span
            >`
          : nothing
      }
      ${
        entry.stepId === 'hiss-reduce' && this.hissRedundant()
          ? html`<p class="voice-editor__hint" data-testid="hiss-hint">
              Denoise already handles most hiss; try one without the other first.
            </p>`
          : nothing
      }
    </div>`;
  }

  private dialsTemplate(busy: boolean) {
    const entry = this.selected();
    if (!entry) return html`<p class="voice-editor__blurb">Select a step to adjust its dials.</p>`;
    return html`<h3>${entry.label}</h3>
      ${
        entry.dials.length === 0
          ? html`<p class="voice-editor__blurb">This step has nothing to adjust.</p>`
          : html`<r2m-settings-form
              mode="defaults"
              .schema=${toStepSchema(entry)}
              .values=${this.values()[entry.stepId] ?? {}}
              .disabled=${busy}
              @values-change=${(e: CustomEvent<SettingsValues>) =>
                this.setValues(entry.stepId, e.detail)}
            ></r2m-settings-form>`
      }`;
  }

  labelFor(stepId: string): string {
    return this.catalog().find((e) => e.stepId === stepId)?.label ?? stepId;
  }

  tick(stepId: string, checked: boolean): void {
    this.ticks.set({ ...this.ticks(), [stepId]: checked });
    this.selectedStepId.set(stepId);
  }

  setValues(stepId: string, values: SettingsValues): void {
    this.values.set({ ...this.values(), [stepId]: values });
  }

  async preview(): Promise<void> {
    const voice = this.voice();
    if (!voice || !this.anyTicked() || this.busy()) return;
    const request = this.currentRequest();
    await this.run(this.rendering, async () => {
      const response = await this.editor.preview(this.folder(), voice.id, request);
      this.lastRender.set({ request, response });
    });
  }

  async apply(): Promise<void> {
    const voice = this.voice();
    const last = this.lastRender();
    if (!voice || !last || this.applyBlocked() !== null) return;
    await this.run(this.writing, async () => {
      this.voice.set(await this.editor.apply(this.folder(), voice.id, last.response.previewId));
      this.audioVersion.update((v) => v + 1);
      this.actionNote.set('Voice audio updated.');
    });
  }

  async restore(): Promise<void> {
    const voice = this.voice();
    if (!voice || this.busy()) return;
    const ok = await this.confirm.confirm({
      title: 'Restore original audio?',
      message: 'The edited audio is discarded and the voice goes back to its original recording.',
      confirmLabel: 'Restore',
      destructive: true,
    });
    if (!ok) return;
    await this.run(this.writing, async () => {
      this.voice.set(await this.editor.restore(this.folder(), voice.id));
      this.resetChain();
      this.audioVersion.update((v) => v + 1);
      this.actionNote.set('Original audio restored.');
    });
  }

  /** One request frame: raise `flag` while it runs, clear the last outcome, keep a failure inline. */
  private async run(flag: WritableSignal<boolean>, work: () => Promise<void>): Promise<void> {
    flag.set(true);
    this.actionError.set(null);
    this.actionNote.set(null);
    try {
      await work();
    } catch (e) {
      this.actionError.set(toApiError(e).message);
    } finally {
      flag.set(false);
    }
  }

  private resetChain(): void {
    this.ticks.set({});
    this.values.set(seedValues(this.catalog()));
    this.lastRender.set(null);
  }

  private async load(folder: string, voiceId: string): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    this.voice.set(null);
    this.lastRender.set(null);
    try {
      const [voice, catalog] = await Promise.all([
        this.voices.get(folder, voiceId),
        this.editor.catalog(),
      ]);
      this.catalog.set(catalog);
      this.resetChain();
      this.selectedStepId.set(catalog[0]?.stepId ?? null);
      if (!voice.audioFileName) {
        this.loadError.set('This voice has no audio to edit.');
        return;
      }
      this.voice.set(voice);
    } catch (e) {
      const error = toApiError(e);
      this.loadError.set(error.status === 404 ? 'That voice no longer exists.' : error.message);
    } finally {
      this.loading.set(false);
    }
  }
}
define('r2m-voice-editor-page', VoiceEditorPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-voice-editor-page': VoiceEditorPage;
  }
}
