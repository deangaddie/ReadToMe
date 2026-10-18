import { html, nothing } from 'lit-html';
import { AttributionApi, AudioApi, BookApi, ProjectsApi, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService, confirmPermanent } from '@app/ui/dialogs';
import { statusChip } from '@app/ui/partials';
import { type PipelineActionEvent, type PipelineCheckpointEvent, pipeline } from '@app/ui/pipeline';
import { ToastService } from '@app/ui/toast';
import { addPausesChange } from '../book/permanent-changes';
import { helpPanel } from './help-panel';
import { rereadConfirm } from './permanent-changes';
import type { StepActionId, StepId } from './pipeline-steps';
import { ProjectStore } from './project-store';
import './project-details-panel';
import overviewCss from './overview-page.css' with { type: 'text' };

adoptStyles(overviewCss);

/**
 * `/projects/{folder}` (design §6.2, guidance-primitives spec §3): the nine-step Pipeline stepper
 * beside the project card. The steps are the {@link ProjectStore}'s `pipeline`; actions post a
 * command and let the receipt (or the HTTP response, when the hub is down) refresh the counts, or
 * navigate to the page that does the work. Checkpoint pills PUT their state. A row's `?` opens that
 * step's help: in the right column in place of the details card at laptop width, inside the row
 * below 1100 px; both render and the CSS shows one. Reads the store the project shell provides.
 */
export class OverviewPage extends R2mElement {
  private store!: ProjectStore;
  private readonly projects = use(ProjectsApi);
  private readonly book = use(BookApi);
  private readonly attribution = use(AttributionApi);
  private readonly audio = use(AudioApi);
  private readonly preflight = use(Preflight);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);
  private readonly router = use(Router);

  /** `step:action` (or `step:pill`) of the command in flight; the stepper disables its controls meanwhile. */
  readonly busy = signal<string | null>(null);
  /** The one step whose help is open, or null. */
  readonly helpOpen = signal<StepId | null>(null);

  protected override connected(): void {
    this.classList.add('overview');
    this.store = use(ProjectStore, this);
  }

  protected template() {
    const detail = this.store.detail();
    const error = this.store.error();
    const steps = this.store.pipeline();
    const author = detail?.author;
    const helpStep = steps?.find((s) => s.id === this.helpOpen()) ?? null;
    const closeHelp = () => this.helpOpen.set(null);
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

      <div class="overview__body ${helpStep ? 'overview__body--help-open' : ''}">
        <section class="overview__pipeline" aria-label="Production pipeline">
          ${
            steps
              ? pipeline({
                  steps,
                  busyAction: this.busy(),
                  helpOpen: this.helpOpen(),
                  onAct: (e) => void this.onAction(e),
                  onCheckpoint: (e) => void this.onCheckpoint(e),
                  onHelp: (id) => this.helpOpen.set(this.helpOpen() === id ? null : id),
                  renderHelp: (step) => helpPanel(step, closeHelp, 'inline'),
                })
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
          ${helpStep ? helpPanel(helpStep, closeHelp, 'side') : nothing}
          <div class="overview__card"><r2m-project-details-panel></r2m-project-details-panel></div>
        </aside>
      </div>
    `;
  }

  async onAction({ step, action }: PipelineActionEvent<StepId>): Promise<void> {
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
  async onCheckpoint({ step, pill }: PipelineCheckpointEvent<StepId>): Promise<void> {
    const folder = this.store.folder();
    const target = this.store
      .pipeline()
      ?.find((s) => s.id === step)
      ?.checkpoint.find((p) => p.id === pill);
    if (!folder || !target) return;
    return this.run(step, pill, () => this.projects.setCheckpoint(folder, step, target.state));
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
