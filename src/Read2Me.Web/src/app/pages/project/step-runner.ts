import { AttributionApi, AudioApi, BookApi, ProjectsApi, toApiError } from '@app/api';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService, confirmPermanent } from '@app/ui/dialogs';
import type { PipelineActionEvent, PipelineCheckpointEvent } from '@app/ui/pipeline';
import { ToastService } from '@app/ui/toast';
import { addPausesChange } from '../book/permanent-changes';
import { rereadConfirm } from './permanent-changes';
import type { StepActionId, StepId } from './pipeline-steps';
import type { ProjectStore } from './project-store';

/**
 * Runs a Pipeline step's actions and checkpoint pills (guidance-primitives spec §2, §4), for the
 * Overview rows and the next-step pill alike, so both take the same path (confirms included).
 * Provided by the project shell beside the {@link ProjectStore}; actions post a command and let
 * the receipt (or the refetch here, when the hub is down) refresh the counts, or navigate to the
 * page that does the work.
 */
export class StepRunner {
  private readonly projects = use(ProjectsApi);
  private readonly book = use(BookApi);
  private readonly attribution = use(AttributionApi);
  private readonly audio = use(AudioApi);
  private readonly preflight = use(Preflight);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private readonly router = use(Router);

  private readonly _busy = signal<string | null>(null);
  /** `step:action` (or `step:pill`) of the command in flight; every surface disables its controls meanwhile. */
  readonly busy = this._busy.asReadonly();

  constructor(private readonly store: ProjectStore) {}

  async act({ step, action }: PipelineActionEvent<StepId>): Promise<void> {
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
        if (!(await this.confirm.confirm(rereadConfirm(this.store.status()?.checkpoints ?? {}))))
          return;
        return this.run(step, action, async () => {
          await this.projects.import(folder, true);
          this.toast.success('Book reread');
        });
      case 'checkStructure':
      case 'reviewText':
        this.router.navigate(`${base}/book`, { query: { mode: 'read' } });
        return;
      case 'manualReread':
        this.router.navigate(`${base}/book`, { query: { action: 'manual-reread' } });
        return;
      case 'editWithAi':
        this.router.navigate(`${base}/book`, { query: { action: 'edit-ai' } });
        return;
      case 'addTitles':
        this.router.navigate(`${base}/book`, { query: { action: 'titles' } });
        return;
      case 'addPauses':
        return this.addPauses(folder, step, action);
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

  /** A pill PUTs its state; the receipt refreshes the row (and `run` refetches for a silent hub). */
  async checkpoint({ step, pill }: PipelineCheckpointEvent<StepId>): Promise<void> {
    const folder = this.store.folder();
    const target = this.store
      .pipeline()
      ?.find((s) => s.id === step)
      ?.checkpoint.find((p) => p.id === pill);
    if (!folder || !target) return;
    return this.run(step, pill, () => this.projects.setCheckpoint(folder, step, target.state));
  }

  /** The project's Overview, where a waiting step's chip and every row live. */
  openOverview(): void {
    const folder = this.store.folder();
    if (folder) this.router.navigate(`projects/${encodeURIComponent(folder)}`);
  }

  /**
   * Add pauses in place (spec §2, §7): the chapter count comes from the book overview, the
   * permanent-change confirm always asks, then the command runs and the receipt refreshes the row.
   */
  private async addPauses(folder: string, step: string, action: string): Promise<void> {
    let chapters: number;
    try {
      chapters = (await this.book.overview(folder)).totalChapters;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return;
    }
    if (!(await confirmPermanent(addPausesChange(chapters)))) return;
    return this.run(step, action, async () => {
      await this.book.execute(folder, { type: 'AddPauses' });
      this.toast.success('Pauses added');
    });
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

  /** Runs one command with every surface busy; failures become a ProblemDetails toast. */
  private async run(step: string, action: string, command: () => Promise<void>): Promise<void> {
    this._busy.set(`${step}:${action}`);
    try {
      await command();
      // The receipt refetches too; this keeps the stepper right when the hub is disconnected.
      await this.store.refreshStatus();
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this._busy.set(null);
    }
  }
}
