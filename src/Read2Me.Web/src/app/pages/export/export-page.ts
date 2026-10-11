import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import {
  AssemblyApi,
  type AssemblyOutputDto,
  AudioProcessingApi,
  assemblyOutputUrl,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { ConfirmService } from '@app/ui/dialogs';
import { type KeyValueRow, keyValue } from '@app/ui/key-value';
import { emptyState, icon, spinner, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import { ProjectStore } from '../project/project-store';
import {
  type PhaseStep,
  type RunOutcome,
  formatBytes,
  missingAudioCount,
  partialPrompt,
  phaseSteps,
  runBanner,
} from './export-model';
import exportCss from './export-page.css' with { type: 'text' };

adoptStyles(exportCss);

/** Angular's `date: 'medium'` under its default `en-US` locale: "Sep 19, 2026, 10:00:00 AM". */
export function formatOutputDate(iso: string): string {
  return new Date(iso).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'medium' });
}

/**
 * `/projects/{folder}/export` (ticket 20): readiness, assemble (full or partial), the live phase
 * stepper with encode progress and cancel, and the project's assembled audiobooks to download or
 * delete. Assembly is one global run, so another project's run disables Assemble here; progress
 * comes from the hub's `assembly` family, never from polling. Reads the folder from the project
 * shell's {@link ProjectStore}, resolved in `connected()`; the computeds below that read the store
 * are lazy, so the render that follows is their first evaluation.
 */
export class ExportPage extends R2mElement {
  private readonly live = use(LiveService);
  private readonly api = use(AssemblyApi);
  private readonly audioProcessing = use(AudioProcessingApi);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private store!: ProjectStore;

  readonly assembly = this.live.assembly;
  readonly outputs = signal<AssemblyOutputDto[]>([]);
  readonly outputsError = signal<string | null>(null);
  /** False until the first list for this project has arrived, so "none yet" is never a guess. */
  readonly outputsLoaded = signal(false);
  readonly starting = signal(false);
  readonly cancelling = signal(false);

  /** `undefined` until (and unless) the settings row loads; blank means ffmpeg is looked up on PATH. */
  private readonly ffmpegPath = signal<string | undefined>(undefined);
  /** How the run this page watched ended, and the phase it had reached (the state forgets both). */
  private readonly watchedOutcome = signal<RunOutcome | null>(null);
  private readonly reachedPhase = signal<string | null>(null);

  /** The global run is this project's. A host that names no folder is given the benefit of the doubt. */
  readonly isThisProjectsRun = computed(() => {
    const folder = this.assembly().folder;
    return folder == null || folder === this.store.folder();
  });
  readonly otherProjectRunning = computed(
    () => this.assembly().isRunning && !this.isThisProjectsRun(),
  );
  /** This project's run is going: the stepper is live, Cancel is offered. */
  readonly running = computed(() => this.isThisProjectsRun() && this.assembly().isRunning);

  readonly audioRemaining = computed(() => this.store.folderAudioRemaining());

  readonly canAssemble = computed(
    () => !!this.store.status()?.hasContent && !this.assembly().isRunning && !this.starting(),
  );

  /** After a reload the hub snapshot still says how the last run ended, bar a cancel. */
  readonly outcome = computed<RunOutcome | null>(() => {
    const watched = this.watchedOutcome();
    if (watched) return watched;
    const state = this.assembly();
    if (!this.isThisProjectsRun() || state.isRunning || state.folder == null) return null;
    if (state.outputFileName) return 'completed';
    return state.lastError ? 'failed' : null;
  });

  readonly steps = computed(() => {
    const running = this.running();
    return phaseSteps({
      isRunning: running,
      phase: running ? this.assembly().currentPhase : this.reachedPhase(),
      outcome: this.outcome(),
    });
  });

  readonly banner = computed(() =>
    runBanner({
      isRunning: this.running(),
      outcome: this.outcome(),
      error: this.assembly().lastError,
      outputFileName: this.assembly().outputFileName,
    }),
  );

  readonly showProgress = computed(() => this.running() || this.outcome() !== null);
  readonly encoding = computed(() => this.running() && this.assembly().currentPhase === 'Encode');
  readonly encodePercent = computed(() => Math.round(this.assembly().encodePercent));

  readonly readiness = computed<KeyValueRow[]>(() => {
    const items = this.store.status()?.items;
    const ffmpeg = this.ffmpegPath();
    return [
      { label: 'Items with audio', value: items ? `${items.withAudio} of ${items.total}` : null },
      { label: 'Paragraphs missing audio', value: this.audioRemaining() },
      {
        label: 'ffmpeg',
        value:
          ffmpeg === undefined ? null : ffmpeg.trim() || 'No path configured — looked up on PATH',
        mono: !!ffmpeg?.trim(),
      },
    ];
  });

  protected override connected(): void {
    this.classList.add('export');
    this.store = use(ProjectStore, this);

    this.effect(() => {
      const folder = this.store.folder();
      untracked(() => {
        this.watchedOutcome.set(null);
        this.reachedPhase.set(null);
        this.outputs.set([]);
        this.outputsLoaded.set(false);
        if (folder) void this.loadOutputs();
      });
    });

    this.onDisconnect(
      this.live.on('assembly', (m) => {
        if ((m.folder ?? this.store.folder()) !== this.store.folder()) return;
        switch (m.kind) {
          case 'phaseStarted':
            this.watchedOutcome.set(null);
            this.reachedPhase.set(m.phase ?? null);
            break;
          case 'progress':
            break;
          case 'completed':
            this.watchedOutcome.set(m.kind);
            void this.loadOutputs();
            break;
          default:
            this.watchedOutcome.set(m.kind);
        }
      }),
    );

    void this.audioProcessing
      .get()
      .then((settings) => this.ffmpegPath.set(settings.ffmpegPath ?? ''))
      // Unknown stays unknown (an em dash): "on PATH" is only said of a row that loaded blank.
      .catch(() => undefined);
  }

  protected template() {
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">Export</h1>
        <p class="r2m-page-header__subtitle">Assemble the .m4b audiobook and download it</p>
      </header>

      <div class="export__cards">
        ${this.readinessTemplate()} ${this.showProgress() ? this.progressTemplate() : nothing}
        ${this.outputsTemplate()}
      </div>
    `;
  }

  private readinessTemplate() {
    return html`<section
      class="export__card"
      aria-labelledby="export-readiness"
      data-card="readiness"
    >
      <h2 class="export__heading" id="export-readiness">Readiness</h2>
      ${keyValue(this.readiness(), { dense: true })}
      <p class="export__hint">
        Assembly only needs ffmpeg — no TTS or LLM service has to be running.
        <a href="settings/audio">Audio processing settings</a>
      </p>
      ${
        this.otherProjectRunning()
          ? statusChip({
              status: 'busy',
              label: `Another project is being assembled: ${this.assembly().folder}`,
            })
          : nothing
      }
      <div class="export__actions">
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          data-action="assemble"
          ?disabled=${!this.canAssemble()}
          @click=${() => void this.assemble()}
        >
          ${this.starting() ? spinner(18) : icon('library_music')}
          ${this.audioRemaining() > 0 ? 'Assemble…' : 'Assemble'}
        </button>
      </div>
    </section>`;
  }

  private progressTemplate() {
    const banner = this.banner();
    const error = this.assembly().lastError;
    return html`<section
      class="export__card"
      aria-labelledby="export-progress"
      data-card="progress"
    >
      <h2 class="export__heading" id="export-progress">Progress</h2>
      <ol class="export__phases">
        ${repeat(
          this.steps(),
          (step) => step.phase,
          (step) => this.phaseTemplate(step),
        )}
      </ol>
      ${
        this.encoding()
          ? html`<progress
              max="1"
              value=${this.assembly().encodePercent / 100}
              aria-label="Encode progress"
            ></progress>`
          : nothing
      }
      ${
        banner
          ? html`<span data-role="outcome"
              >${statusChip({ status: banner.status, label: banner.label })}</span
            >`
          : nothing
      }
      ${
        this.outcome() === 'failed' && error
          ? html`<pre class="export__error">${error}</pre>`
          : nothing
      }
      ${
        this.running()
          ? html`<div class="export__actions">
              <button
                type="button"
                class="r2m-button r2m-button--stroked"
                data-action="cancel-assembly"
                ?disabled=${this.cancelling()}
                @click=${() => void this.cancel()}
              >
                ${icon('stop_circle')} Cancel
              </button>
            </div>`
          : nothing
      }
    </section>`;
  }

  private phaseTemplate(step: PhaseStep) {
    return html`<li
      class="export__phase export__phase--${step.state}"
      data-phase=${step.phase}
      data-state=${step.state}
      aria-current=${step.state === 'active' ? 'step' : nothing}
    >
      <span class="export__marker" aria-hidden="true">${this.markerTemplate(step)}</span>
      <span class="export__phase-label">${step.label}</span>
      ${
        step.phase === 'Encode' && step.state === 'active'
          ? html`<span class="export__percent" data-role="encode-percent"
              >${this.encodePercent()}%</span
            >`
          : nothing
      }
    </li>`;
  }

  private markerTemplate(step: PhaseStep) {
    switch (step.state) {
      case 'done':
        return icon('check');
      case 'active':
        return spinner(16);
      case 'failed':
        return icon('error');
      case 'cancelled':
        return icon('block');
      default:
        return nothing;
    }
  }

  private outputsTemplate() {
    const error = this.outputsError();
    const outputs = this.outputs();
    return html`<section class="export__card" aria-labelledby="export-outputs" data-card="outputs">
      <h2 class="export__heading" id="export-outputs">Outputs</h2>
      ${
        error !== null
          ? statusChip({ status: 'error', label: `Outputs could not be loaded: ${error}` })
          : !this.outputsLoaded()
            ? html`<div
                class="export__skeleton"
                aria-busy="true"
                aria-label="Loading outputs"
              ></div>`
            : outputs.length === 0
              ? emptyState({
                  icon: 'library_music',
                  headline: 'No audiobooks yet',
                  hint: 'Assembled .m4b files appear here.',
                  compact: true,
                })
              : html`<ul class="export__outputs">
                  ${repeat(
                    outputs,
                    (output) => output.fileName,
                    (output) => this.outputTemplate(output),
                  )}
                </ul>`
      }
    </section>`;
  }

  private outputTemplate(output: AssemblyOutputDto) {
    return html`<li class="export__output" data-output=${output.fileName}>
      ${icon('audio_file', 'export__output-icon')}
      <div class="export__output-main">
        <span class="export__output-name">${output.fileName}</span>
        <span class="export__output-meta">
          ${formatBytes(output.sizeBytes)} · ${formatOutputDate(output.createdAt)}
        </span>
      </div>
      ${output.isPartial ? statusChip({ status: 'warn', label: 'Partial', compact: true }) : nothing}
      <a
        class="r2m-button"
        data-action="download-output"
        href=${this.downloadUrl(output)}
        download=${output.fileName}
      >
        ${icon('download')} Download
      </a>
      <button
        type="button"
        class="r2m-icon-button"
        data-action="delete-output"
        aria-label=${`Delete ${output.fileName}`}
        @click=${() => void this.deleteOutput(output)}
      >
        ${icon('delete')}
      </button>
    </li>`;
  }

  downloadUrl(output: AssemblyOutputDto): string {
    return assemblyOutputUrl(this.store.folder() ?? '', output.fileName);
  }

  /**
   * Always asks for a full build first: the host's 409 carries the exact count of items without
   * audio, which is what the partial prompt has to name.
   */
  async assemble(): Promise<void> {
    const folder = this.store.folder();
    if (!folder || !this.canAssemble()) return;

    this.starting.set(true);
    try {
      try {
        await this.api.start(folder, false);
      } catch (error) {
        const missing = missingAudioCount(error);
        if (missing === null) throw error;
        if (!(await this.confirm.confirm(partialPrompt(missing)))) return;
        await this.api.start(folder, true);
      }
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.starting.set(false);
    }
  }

  async cancel(): Promise<void> {
    this.cancelling.set(true);
    try {
      await this.api.cancel();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.cancelling.set(false);
    }
  }

  async deleteOutput(output: AssemblyOutputDto): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    const confirmed = await this.confirm.confirm({
      title: 'Delete this audiobook?',
      message: `${output.fileName} is removed from the project's output folder. This cannot be undone.`,
      confirmLabel: 'Delete',
      destructive: true,
    });
    if (!confirmed) return;
    try {
      await this.api.deleteOutput(folder, output.fileName);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    }
    await this.loadOutputs();
  }

  private async loadOutputs(): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    try {
      const outputs = await this.api.outputs(folder);
      if (this.store.folder() !== folder) return;
      this.outputs.set(outputs);
      this.outputsLoaded.set(true);
      this.outputsError.set(null);
    } catch (error) {
      if (this.store.folder() === folder) this.outputsError.set(toApiError(error).message);
    }
  }
}
define('r2m-export-page', ExportPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-export-page': ExportPage;
  }
}
