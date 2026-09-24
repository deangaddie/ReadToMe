import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { JobCard } from '@app/ui/job-card/job-card';
import { Throughput } from '@app/ui/throughput/throughput';
import { ActivityStore } from './activity-store';

/**
 * The drawer's Jobs tab (ticket 14): one `r2m-job-card` per job with Cancel / Dismiss, then the
 * LLM throughput block — headline and sparkline while a run is active, the per-config table once it
 * has ended, and Dismiss to retire it (Blazor's StatusDock Dismiss).
 */
@Component({
  selector: 'app-jobs-tab',
  imports: [MatButtonModule, MatIconModule, EmptyState, JobCard, Throughput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-jobs' },
  template: `
    @if (store.jobs().length === 0 && !store.showThroughput()) {
      <r2m-empty-state
        compact
        icon="pending_actions"
        headline="No background work"
        hint="Attribution, audio generation, voice batches and assembly show up here while they run."
      />
    }

    @for (job of store.jobs(); track job.id) {
      <r2m-job-card [job]="job" (cancel)="store.cancel($event)" (dismiss)="store.dismiss($event)" />
    }

    @if (store.showThroughput() && store.throughput(); as t) {
      <section class="activity-jobs__throughput" aria-label="LLM throughput">
        <header class="activity-jobs__throughput-head">
          <mat-icon aria-hidden="true">speed</mat-icon>
          <span class="activity-jobs__throughput-title">LLM throughput</span>
        </header>
        <r2m-throughput [snapshot]="t" />
        @if (!t.isRunActive) {
          <footer class="activity-jobs__throughput-actions">
            <button mat-button type="button" (click)="store.dismissThroughput()">Dismiss</button>
          </footer>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      height: 100%;
      min-height: 0;
      overflow: auto;
      padding: var(--r2m-space-3);
    }
    .activity-jobs__throughput {
      padding: var(--r2m-space-3) var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .activity-jobs__throughput-head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .activity-jobs__throughput-head mat-icon {
      color: var(--r2m-accent);
    }
    .activity-jobs__throughput-title {
      flex: 1;
      font-weight: 600;
    }
    .activity-jobs__throughput-actions {
      display: flex;
      justify-content: flex-end;
      margin-top: var(--r2m-space-2);
    }
  `,
})
export class JobsTab {
  protected readonly store = inject(ActivityStore);
}
