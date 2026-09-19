import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RouterLink } from '@angular/router';
import {
  AssemblyApi,
  AssemblyOutputDto,
  AudioProcessingApi,
  assemblyOutputUrl,
  toApiError,
} from '@app/api';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { KeyValue, KeyValueRow } from '@app/ui/key-value/key-value';
import { PageHeader } from '@app/ui/page-header/page-header';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { ProjectStore } from '../project/project-store';
import {
  RunOutcome,
  formatBytes,
  missingAudioCount,
  partialPrompt,
  phaseSteps,
  runBanner,
} from './export-model';

/**
 * `/projects/{folder}/export` (ticket 20): readiness, assemble (full or partial), the live phase
 * stepper with encode progress and cancel, and the project's assembled audiobooks to download or
 * delete. Assembly is one global run, so another project's run disables Assemble here; progress
 * comes from the hub's `assembly` family, never from polling.
 */
@Component({
  selector: 'app-export-page',
  imports: [
    DatePipe,
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    EmptyState,
    KeyValue,
    PageHeader,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header title="Export" subtitle="Assemble the .m4b audiobook and download it" />

    <div class="export">
      <section class="export__card" aria-labelledby="export-readiness" data-card="readiness">
        <h2 class="export__heading" id="export-readiness">Readiness</h2>
        <r2m-key-value dense [rows]="readiness()" />
        <p class="export__hint">
          Assembly only needs ffmpeg — no TTS or LLM service has to be running.
          <a routerLink="/settings/audio">Audio processing settings</a>
        </p>
        @if (otherProjectRunning()) {
          <r2m-status-chip
            status="busy"
            [label]="'Another project is being assembled: ' + assembly().folder"
          />
        }
        <div class="export__actions">
          <button
            mat-flat-button
            type="button"
            data-action="assemble"
            [disabled]="!canAssemble()"
            (click)="assemble()"
          >
            @if (starting()) {
              <mat-spinner diameter="18" />
            } @else {
              <mat-icon>library_music</mat-icon>
            }
            {{ audioRemaining() > 0 ? 'Assemble…' : 'Assemble' }}
          </button>
        </div>
      </section>

      @if (showProgress()) {
        <section class="export__card" aria-labelledby="export-progress" data-card="progress">
          <h2 class="export__heading" id="export-progress">Progress</h2>
          <ol class="export__phases">
            @for (step of steps(); track step.phase) {
              <li
                [class]="'export__phase export__phase--' + step.state"
                [attr.data-phase]="step.phase"
                [attr.data-state]="step.state"
                [attr.aria-current]="step.state === 'active' ? 'step' : null"
              >
                <span class="export__marker" aria-hidden="true">
                  @switch (step.state) {
                    @case ('done') {
                      <mat-icon>check</mat-icon>
                    }
                    @case ('active') {
                      <mat-spinner diameter="16" />
                    }
                    @case ('failed') {
                      <mat-icon>error</mat-icon>
                    }
                    @case ('cancelled') {
                      <mat-icon>block</mat-icon>
                    }
                  }
                </span>
                <span class="export__phase-label">{{ step.label }}</span>
                @if (step.phase === 'Encode' && step.state === 'active') {
                  <span class="export__percent" data-role="encode-percent"
                    >{{ encodePercent() }}%</span
                  >
                }
              </li>
            }
          </ol>
          @if (
            isThisProjectsRun() && assembly().isRunning && assembly().currentPhase === 'Encode'
          ) {
            <mat-progress-bar
              mode="determinate"
              [value]="assembly().encodePercent"
              aria-label="Encode progress"
            />
          }
          @if (banner(); as b) {
            <r2m-status-chip data-role="outcome" [status]="b.status" [label]="b.label" />
          }
          @if (outcome() === 'failed' && assembly().lastError; as error) {
            <pre class="export__error">{{ error }}</pre>
          }
          @if (isThisProjectsRun() && assembly().isRunning) {
            <div class="export__actions">
              <button
                mat-stroked-button
                type="button"
                data-action="cancel-assembly"
                [disabled]="cancelling()"
                (click)="cancel()"
              >
                <mat-icon>stop_circle</mat-icon> Cancel
              </button>
            </div>
          }
        </section>
      }

      <section class="export__card" aria-labelledby="export-outputs" data-card="outputs">
        <h2 class="export__heading" id="export-outputs">Outputs</h2>
        @if (outputsError(); as error) {
          <r2m-status-chip status="error" [label]="'Outputs could not be loaded: ' + error" />
        } @else if (!outputsLoaded()) {
          <div class="export__skeleton" aria-busy="true" aria-label="Loading outputs"></div>
        } @else if (outputs().length === 0) {
          <r2m-empty-state
            compact
            icon="library_music"
            headline="No audiobooks yet"
            hint="Assembled .m4b files appear here."
          />
        } @else {
          <ul class="export__outputs">
            @for (output of outputs(); track output.fileName) {
              <li class="export__output" [attr.data-output]="output.fileName">
                <mat-icon class="export__output-icon" aria-hidden="true">audio_file</mat-icon>
                <div class="export__output-main">
                  <span class="export__output-name">{{ output.fileName }}</span>
                  <span class="export__output-meta">
                    {{ size(output) }} · {{ output.createdAt | date: 'medium' }}
                  </span>
                </div>
                @if (output.isPartial) {
                  <r2m-status-chip status="warn" label="Partial" compact />
                }
                <a
                  mat-button
                  data-action="download-output"
                  [href]="downloadUrl(output)"
                  [attr.download]="output.fileName"
                >
                  <mat-icon>download</mat-icon> Download
                </a>
                <button
                  mat-icon-button
                  type="button"
                  data-action="delete-output"
                  [attr.aria-label]="'Delete ' + output.fileName"
                  (click)="deleteOutput(output)"
                >
                  <mat-icon>delete</mat-icon>
                </button>
              </li>
            }
          </ul>
        }
      </section>
    </div>
  `,
  styles: `
    .export {
      display: grid;
      gap: var(--r2m-space-4);
      max-width: 880px;
      margin-top: var(--r2m-space-3);
    }
    .export__card {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
    }
    .export__heading {
      margin: 0;
      font: var(--mat-sys-title-medium);
    }
    .export__hint {
      margin: 0;
      color: var(--r2m-text-muted);
      font: var(--mat-sys-body-small);
    }
    .export__actions {
      display: flex;
      gap: var(--r2m-space-2);
    }
    .export__phases {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-2) var(--r2m-space-4);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .export__phase {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      color: var(--r2m-text-muted);
    }
    .export__phase--active,
    .export__phase--done {
      color: inherit;
    }
    .export__phase--active .export__phase-label {
      font-weight: 600;
    }
    .export__phase--failed {
      color: var(--mat-sys-error);
    }
    .export__marker {
      display: grid;
      place-items: center;
      width: 22px;
      height: 22px;
      border: 1px solid var(--r2m-outline);
      border-radius: 50%;
    }
    .export__marker mat-icon {
      width: 16px;
      height: 16px;
      font-size: 16px;
    }
    .export__percent {
      font-variant-numeric: tabular-nums;
    }
    .export__error {
      margin: 0;
      max-height: 240px;
      overflow: auto;
      padding: var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      background: color-mix(in srgb, var(--mat-sys-error) 8%, transparent);
      font: var(--mat-sys-body-small);
      font-family: var(--r2m-font-mono, monospace);
      white-space: pre-wrap;
    }
    .export__skeleton {
      height: 56px;
      border-radius: var(--r2m-radius-md);
      background: color-mix(in srgb, var(--r2m-text-muted) 12%, transparent);
    }
    .export__outputs {
      display: flex;
      flex-direction: column;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .export__output {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
      padding: var(--r2m-space-2) 0;
    }
    .export__output + .export__output {
      border-top: 1px solid var(--r2m-outline);
    }
    .export__output-icon {
      color: var(--r2m-text-muted);
    }
    .export__output-main {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-width: 0;
    }
    .export__output-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .export__output-meta {
      color: var(--r2m-text-muted);
      font: var(--mat-sys-body-small);
    }
  `,
})
export class ExportPage {
  readonly store = inject(ProjectStore);
  private readonly live = inject(LiveService);
  private readonly api = inject(AssemblyApi);
  private readonly audioProcessing = inject(AudioProcessingApi);
  private readonly preflight = inject(Preflight);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);

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
    const state = this.assembly();
    const running = this.isThisProjectsRun() && state.isRunning;
    return phaseSteps({
      isRunning: running,
      phase: running ? state.currentPhase : this.reachedPhase(),
      outcome: this.outcome(),
    });
  });

  readonly banner = computed(() =>
    runBanner({
      isRunning: this.isThisProjectsRun() && this.assembly().isRunning,
      outcome: this.outcome(),
      error: this.assembly().lastError,
      outputFileName: this.assembly().outputFileName,
    }),
  );

  readonly showProgress = computed(
    () => (this.isThisProjectsRun() && this.assembly().isRunning) || this.outcome() !== null,
  );

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

  constructor() {
    effect(() => {
      const folder = this.store.folder();
      untracked(() => {
        this.watchedOutcome.set(null);
        this.reachedPhase.set(null);
        this.outputs.set([]);
        this.outputsLoaded.set(false);
        if (folder) void this.loadOutputs();
      });
    });

    this.live
      .on('assembly')
      .pipe(takeUntilDestroyed())
      .subscribe((m) => {
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
      });

    void this.audioProcessing
      .get()
      .then((settings) => this.ffmpegPath.set(settings.ffmpegPath ?? ''))
      // Unknown stays unknown (an em dash): "on PATH" is only said of a row that loaded blank.
      .catch(() => undefined);
  }

  size(output: AssemblyOutputDto): string {
    return formatBytes(output.sizeBytes);
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
    if (!(await this.preflight.ensureReady('assembly'))) return;

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
