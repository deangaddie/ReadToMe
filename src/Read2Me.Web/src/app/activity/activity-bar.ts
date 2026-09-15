import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  inject,
  input,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { JobView } from '@app/ui/job-pill/job';
import { JobPill } from '@app/ui/job-pill/job-pill';
import { ActivityStore } from './activity-store';

/** The narrow-screen summary pill (design §5): one pill standing in for every active job. */
export function summaryJob(jobs: readonly JobView[]): JobView {
  const first = jobs[0]!;
  return jobs.length === 1
    ? first
    : {
        id: 'summary',
        kind: first.kind,
        label: `${jobs.length} jobs`,
        state: 'running',
        detail: jobs.map((j) => j.label).join(' · '),
      };
}

/**
 * The activity bar (design §5): one `r2m-job-pill` per active job with cancel ×, "No background
 * work" when idle, and the ▲ that opens the drawer. Any pill opens the drawer on the Jobs tab.
 */
@Component({
  selector: 'app-activity-bar',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule, JobPill],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-bar' },
  template: `
    @if (pills().length === 0) {
      <span class="activity-bar__idle">
        <mat-icon aria-hidden="true">hourglass_empty</mat-icon>
        <span>No background work</span>
      </span>
    } @else {
      <div class="activity-bar__pills">
        @for (job of pills(); track job.id) {
          <r2m-job-pill
            [job]="job"
            [tooltip]="job.detail ?? ''"
            (open)="store.openDrawer('jobs')"
            (cancel)="store.cancel($event)"
          />
        }
      </div>
    }
    <span class="activity-bar__spacer"></span>
    <button
      mat-icon-button
      type="button"
      (click)="store.toggleDrawer()"
      [attr.aria-expanded]="store.drawerOpen()"
      aria-label="Toggle activity drawer"
      matTooltip="Activity"
    >
      <mat-icon>{{ store.drawerOpen() ? 'expand_more' : 'expand_less' }}</mat-icon>
    </button>
  `,
  styles: `
    :host {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      width: 100%;
      min-width: 0;
    }
    .activity-bar__idle {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-1);
    }
    .activity-bar__idle mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .activity-bar__pills {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      min-width: 0;
      overflow-x: auto;
      scrollbar-width: none;
    }
    .activity-bar__spacer {
      flex: 1 1 auto;
    }
  `,
})
export class ActivityBar {
  protected readonly store = inject(ActivityStore);
  /** Below 900 px the bar collapses to a single summary pill. */
  readonly narrow = input(false, { transform: booleanAttribute });

  protected readonly pills = computed<JobView[]>(() => {
    const jobs = this.store.activeJobs();
    if (jobs.length === 0) return [];
    return this.narrow() ? [summaryJob(jobs)] : jobs;
  });
}
