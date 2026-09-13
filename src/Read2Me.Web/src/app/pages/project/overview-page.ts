import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AttributionApi, AudioApi, ProjectsApi, toApiError } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { PageHeader } from '@app/ui/page-header/page-header';
import { Pipeline, PipelineActionEvent } from '@app/ui/pipeline/pipeline';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { derivePipeline, StepActionId } from './pipeline-steps';
import { ProjectDetailsPanel } from './project-details-panel';
import { ProjectStore } from './project-store';

/**
 * `/projects/{folder}` (design §6.2, ticket 09): the pipeline stepper beside the project card.
 * Steps are derived from the store's `/status` bootstrap plus live hub state; actions post a
 * command and let the receipt (or the HTTP response, when the hub is down) refresh the counts.
 */
@Component({
  selector: 'app-overview-page',
  imports: [PageHeader, Pipeline, StatusChip, ProjectDetailsPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <r2m-page-header
      [title]="store.detail()?.title ?? store.folder() ?? ''"
      [subtitle]="store.detail()?.author || undefined"
    />

    @if (store.error(); as error) {
      <r2m-status-chip status="error" [label]="'Project could not be loaded: ' + error" />
    }

    <div class="overview">
      <section class="overview__pipeline" aria-label="Production pipeline">
        @if (steps(); as steps) {
          <r2m-pipeline [steps]="steps" [busyAction]="busy()" (act)="onAction($event)" />
        } @else if (store.loading()) {
          <div class="overview__skeleton" aria-busy="true" aria-label="Loading pipeline"></div>
        }
      </section>
      <aside class="overview__details" aria-label="Project">
        <app-project-details-panel />
      </aside>
    </div>
  `,
  styles: `
    .overview {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 320px;
      gap: var(--r2m-space-8);
      align-items: start;
      margin-top: var(--r2m-space-3);
    }
    @media (max-width: 1099.98px) {
      .overview {
        grid-template-columns: minmax(0, 1fr);
      }
      .overview__details {
        order: -1;
      }
    }
    .overview__skeleton {
      height: 480px;
      border-radius: var(--r2m-radius-lg);
      background: color-mix(in srgb, var(--r2m-text-muted) 12%, transparent);
    }
  `,
})
export class OverviewPage {
  readonly store = inject(ProjectStore);
  private readonly live = inject(LiveService);
  private readonly projects = inject(ProjectsApi);
  private readonly attribution = inject(AttributionApi);
  private readonly audio = inject(AudioApi);
  private readonly preflight = inject(Preflight);
  private readonly confirm = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  /** `step:action` of the command in flight; the stepper disables its actions meanwhile. */
  readonly busy = signal<string | null>(null);

  readonly steps = computed(() => {
    const status = this.store.status();
    if (!status) return null;
    return derivePipeline({
      status,
      nodes: this.store.nodes(),
      folderAudioRemaining: this.store.folderAudioRemaining(),
      audioInFlight: this.store.audioInFlight(),
      voiceBatchRunning: this.live.voiceBatch().isRunning,
      assemblyRunning: this.live.assembly().isRunning,
    });
  });

  async onAction({ step, action }: PipelineActionEvent): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    const base = ['/projects', folder];
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
        void this.router.navigate([...base, 'cast'], { queryParams: { discover: 1 } });
        return;
      case 'openCast':
        void this.router.navigate([...base, 'cast']);
        return;
      case 'openSpeakers':
        void this.router.navigate([...base, 'book'], { queryParams: { mode: 'speakers' } });
        return;
      case 'openAudioMode':
        void this.router.navigate([...base, 'book'], { queryParams: { mode: 'audio' } });
        return;
      case 'assemble':
        void this.router.navigate([...base, 'export']);
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
