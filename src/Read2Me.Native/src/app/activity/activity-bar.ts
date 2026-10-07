import { html } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import type { JobView } from '@app/ui/job';
import { jobPill } from '@app/ui/job-pill';
import { icon } from '@app/ui/partials';
import { ActivityStore } from './activity-store';
import './styles';

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
 * The activity bar (design §5): one job pill per active job with cancel ×, "No background work"
 * when idle, and the ▲ that opens the drawer. Any pill opens the drawer on the Jobs tab.
 */
export class ActivityBar extends R2mElement {
  private readonly store = use(ActivityStore);

  #narrow = signal(false);
  /** Below 900 px the bar collapses to a single summary pill. */
  get narrow() {
    return this.#narrow();
  }
  set narrow(value: boolean) {
    this.#narrow.set(value);
  }

  private readonly pills = computed<JobView[]>(() => {
    const jobs = this.store.activeJobs();
    if (jobs.length === 0) return [];
    return this.#narrow() ? [summaryJob(jobs)] : jobs;
  });

  protected override connected(): void {
    this.classList.add('activity-bar');
  }

  protected template() {
    const pills = this.pills();
    const open = this.store.drawerOpen();
    return html`
      ${
        pills.length === 0
          ? html`<span class="activity-bar__idle"
              >${icon('hourglass_empty')}<span>No background work</span></span
            >`
          : html`<div class="activity-bar__pills">
              ${repeat(
                pills,
                (job) => job.id,
                (job) =>
                  jobPill({
                    job,
                    tooltip: job.detail ?? '',
                    onOpen: () => this.store.openDrawer('jobs'),
                    onCancel: (id) => void this.store.cancel(id),
                  }),
              )}
            </div>`
      }
      <span class="activity-bar__spacer"></span>
      <button
        type="button"
        class="r2m-icon-button activity-bar__toggle"
        aria-expanded=${open ? 'true' : 'false'}
        aria-label="Toggle activity drawer"
        data-tooltip="Activity"
        @click=${() => this.store.toggleDrawer()}
      >
        ${icon(open ? 'expand_more' : 'expand_less')}
      </button>
    `;
  }
}
define('r2m-activity-bar', ActivityBar);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-activity-bar': ActivityBar;
  }
}
