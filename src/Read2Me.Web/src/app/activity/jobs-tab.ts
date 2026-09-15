import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { JobCard } from '@app/ui/job-card/job-card';
import { Sparkline } from '@app/ui/sparkline/sparkline';
import { ActivityStore } from './activity-store';

/**
 * The drawer's Jobs tab (ticket 14): one `r2m-job-card` per job with Cancel / Dismiss, then the
 * LLM throughput block — headline and sparkline while a run is active, the per-config table once it
 * has ended, and Dismiss to retire it (Blazor's StatusDock Dismiss).
 */
@Component({
  selector: 'app-jobs-tab',
  imports: [MatButtonModule, MatIconModule, EmptyState, JobCard, Sparkline],
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
          <span class="activity-jobs__throughput-headline">
            @if (t.isRunActive && t.generationRate !== null && t.generationRate !== undefined) {
              {{ t.generationRate.toFixed(1) }} tok/s now
            } @else if (t.runThroughput !== null && t.runThroughput !== undefined) {
              {{ t.runThroughput.toFixed(1) }} tok/s over the run
            }
          </span>
        </header>
        @if (store.throughputHistory().length) {
          <r2m-sparkline
            class="activity-jobs__sparkline"
            [values]="store.throughputHistory()"
            [width]="360"
            [height]="32"
            label="Generation rate, last 10 seconds"
          />
        }
        @if (store.showThroughputTable() && t.perConfig.length) {
          <table class="activity-jobs__table">
            <thead>
              <tr>
                <th scope="col">Config</th>
                <th scope="col" class="activity-jobs__num">Requests</th>
                <th scope="col" class="activity-jobs__num">Tokens out</th>
                <th scope="col" class="activity-jobs__num">Gen time</th>
                <th scope="col" class="activity-jobs__num">tok/s</th>
              </tr>
            </thead>
            <tbody>
              @for (row of t.perConfig; track row.configId) {
                <tr>
                  <td>{{ row.configName }}</td>
                  <td class="activity-jobs__num">{{ row.requests }}</td>
                  <td class="activity-jobs__num">{{ row.tokensOut ?? '–' }}</td>
                  <td class="activity-jobs__num">{{ seconds(row.generationMs) }}</td>
                  <td class="activity-jobs__num">{{ row.tokensPerSecond?.toFixed(1) ?? '–' }}</td>
                </tr>
              }
            </tbody>
          </table>
        }
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
    .activity-jobs__throughput-headline {
      font-variant-numeric: tabular-nums;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .activity-jobs__sparkline {
      display: block;
      width: 100%;
      margin-top: var(--r2m-space-2);
    }
    .activity-jobs__sparkline ::ng-deep svg {
      width: 100%;
      height: 32px;
    }
    .activity-jobs__table {
      width: 100%;
      margin-top: var(--r2m-space-2);
      border-collapse: collapse;
      font-size: var(--r2m-text-sm);
    }
    .activity-jobs__table th,
    .activity-jobs__table td {
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border-bottom: 1px solid var(--r2m-outline);
      text-align: left;
    }
    .activity-jobs__table th {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .activity-jobs__num {
      text-align: right !important;
      font-variant-numeric: tabular-nums;
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

  protected seconds(ms: number | null | undefined): string {
    return ms == null ? '–' : `${(ms / 1000).toFixed(1)} s`;
  }
}
