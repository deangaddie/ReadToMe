import { html, nothing } from 'lit-html';
import {
  AssemblyApi,
  type AssemblyOutputDto,
  AttributionApi,
  AudioApi,
  ProjectsApi,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { type ReadonlySignal, computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/dialogs';
import { statusChip } from '@app/ui/partials';
import { type PipelineActionEvent, pipeline } from '@app/ui/pipeline';
import { ToastService } from '@app/ui/toast';
import { type PipelineStep, type StepActionId, derivePipeline } from './pipeline-steps';
import { ProjectStore } from './project-store';
import './project-details-panel';
import overviewCss from './overview-page.css' with { type: 'text' };

adoptStyles(overviewCss);

/** `formatDate(…, 'mediumDate')`: "19 Sep 2026" in the browser's locale. */
export function formatBuildDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  });
}

/**
 * `/projects/{folder}` (design §6.2): the pipeline stepper beside the project card. Steps are
 * derived from the store's `/status` bootstrap plus live hub state; actions post a command and let
 * the receipt (or the HTTP response, when the hub is down) refresh the counts. Reads the
 * {@link ProjectStore} the project shell provides.
 */
export class OverviewPage extends R2mElement {
  private store!: ProjectStore;
  private readonly live = use(LiveService);
  private readonly projects = use(ProjectsApi);
  private readonly attribution = use(AttributionApi);
  private readonly audio = use(AudioApi);
  private readonly assembly = use(AssemblyApi);
  private readonly preflight = use(Preflight);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private readonly router = use(Router);

  /** `step:action` of the command in flight; the stepper disables its actions meanwhile. */
  readonly busy = signal<string | null>(null);

  /** Newest assembled audiobook, for the Export step's "Last build". */
  private readonly lastOutput = signal<AssemblyOutputDto | null>(null);

  /** Built in `connected()`, once the shell's store is reachable through the DOM. */
  private steps!: ReadonlySignal<PipelineStep[] | null>;

  protected override connected(): void {
    this.classList.add('overview');
    this.store = use(ProjectStore, this);
    const lastBuild = computed(() => {
      const output = this.lastOutput();
      return output ? { date: formatBuildDate(output.createdAt), isPartial: output.isPartial } : null;
    });
    this.steps = computed(() => {
      const status = this.store.status();
      if (!status) return null;
      return derivePipeline({
        status,
        nodes: this.store.nodes(),
        folderAudioRemaining: this.store.folderAudioRemaining(),
        audioInFlight: this.store.audioInFlight(),
        voiceBatchRunning: this.live.voiceBatch().isRunning,
        assemblyRunning: this.live.assembly().isRunning,
        lastBuild: lastBuild(),
      });
    });
    this.effect(() => {
      const folder = this.store.folder();
      untracked(() => {
        this.lastOutput.set(null);
        if (folder) void this.loadLastOutput(folder);
      });
    });
    this.onDisconnect(
      this.live.on('assembly', (m) => {
        const folder = this.store.folder();
        if (m.kind === 'completed' && folder && (m.folder ?? folder) === folder)
          void this.loadLastOutput(folder);
      }),
    );
  }

  protected template() {
    const detail = this.store.detail();
    const error = this.store.error();
    const steps = this.steps();
    const author = detail?.author;
    return html`
      <header class="r2m-page-header">
        <h1 class="r2m-page-header__title">${detail?.title ?? this.store.folder() ?? ''}</h1>
        ${author ? html`<p class="r2m-page-header__subtitle">${author}</p>` : nothing}
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Project could not be loaded: ${error}` })
          : nothing
      }

      <div class="overview__body">
        <section class="overview__pipeline" aria-label="Production pipeline">
          ${
            steps
              ? pipeline({ steps, busyAction: this.busy(), onAct: (e) => void this.onAction(e) })
              : this.store.loading()
                ? html`<div
                    class="overview__skeleton"
                    aria-busy="true"
                    aria-label="Loading pipeline"
                  ></div>`
                : nothing
          }
        </section>
        <aside class="overview__details" aria-label="Project">
          <r2m-project-details-panel></r2m-project-details-panel>
        </aside>
      </div>
    `;
  }

  /** The stepper works without it, so a failure just leaves the step saying nothing was built. */
  private async loadLastOutput(folder: string): Promise<void> {
    try {
      const [newest] = await this.assembly.outputs(folder);
      if (this.store.folder() === folder) this.lastOutput.set(newest ?? null);
    } catch {
      // Left as it was.
    }
  }

  async onAction({ step, action }: PipelineActionEvent): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    const base = `projects/${encodeURIComponent(folder)}`;
    switch (action as StepActionId) {
      case 'readBook':
        return this.run(step, action, async () => {
          await this.projects.import(folder);
          this.toast.success('Book read in');
        });
      case 'reread':
        if (
          !(await this.confirm.confirm({
            title: 'Reread the book?',
            message:
              'This deletes all book data — attribution, generated audio, and edits — and ' +
              'reprocesses the source file from scratch. This cannot be undone.',
            destructive: true,
            confirmLabel: 'Delete and reread',
          }))
        )
          return;
        return this.run(step, action, async () => {
          await this.projects.import(folder, true);
          this.toast.success('Book reread');
        });
      case 'discover':
        this.router.navigate(`${base}/cast`, { query: { discover: '1' } });
        return;
      case 'openCast':
        this.router.navigate(`${base}/cast`);
        return;
      case 'openSpeakers':
        this.router.navigate(`${base}/book`, { query: { mode: 'speakers' } });
        return;
      case 'openAudioMode':
        this.router.navigate(`${base}/book`, { query: { mode: 'audio' } });
        return;
      case 'assemble':
        this.router.navigate(`${base}/export`);
        return;
      case 'attribute':
        return this.run(step, action, async () => {
          if (!(await this.preflight.ensureReady('attribution'))) return;
          await this.enqueueEveryVolume('paragraph', (nodeId) =>
            this.attribution.enqueue(folder, { level: 'volume', nodeId, unprocessedOnly: true }),
          );
        });
      case 'generateAudio':
        return this.run(step, action, async () => {
          if (!(await this.preflight.ensureReady('audio'))) return;
          const narratorOnlyMode = this.store.detail()?.narratorOnlyMode ?? false;
          await this.enqueueEveryVolume('item', (nodeId) =>
            this.audio.enqueue(folder, {
              level: 'volume',
              nodeId,
              needsAudioOnly: true,
              narratorOnlyMode,
            }),
          );
        });
    }
  }

  /**
   * One enqueue per volume, awaited in book order: the queue works in arrival order, so parallel
   * requests could start volume 3 before volume 1.
   */
  private async enqueueEveryVolume(
    unit: string,
    enqueue: (volumeId: string) => Promise<{ enqueued: number }>,
  ): Promise<void> {
    let total = 0;
    for (const volumeId of this.store.status()?.volumeIds ?? [])
      total += (await enqueue(volumeId)).enqueued;
    this.toast.success(
      total === 0 ? 'Nothing to queue' : `Queued ${total} ${unit}${total === 1 ? '' : 's'}`,
    );
  }

  /** Runs one command with the stepper busy; failures become a ProblemDetails toast. */
  private async run(step: string, action: string, command: () => Promise<void>): Promise<void> {
    this.busy.set(`${step}:${action}`);
    try {
      await command();
      // The receipt refetches too; this keeps the stepper right when the hub is disconnected.
      await this.store.refreshStatus();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.busy.set(null);
    }
  }
}
define('r2m-overview-page', OverviewPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-overview-page': OverviewPage;
  }
}
