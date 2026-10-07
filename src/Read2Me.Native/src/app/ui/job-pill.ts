import { html, nothing } from 'lit-html';
import { JOB_ICONS, JOB_STATE_STATUS, type JobView, formatDuration, jobSummary } from './job';
import { icon, spinner, statusChip } from './partials';
import { sparkline } from './throughput';

export interface JobPillOptions {
  job: JobView;
  /** The whole pill opens the activity drawer. */
  onOpen: (id: string) => void;
  onCancel: (id: string) => void;
  tooltip?: string;
}

/**
 * Compact one-line job status for the activity bar (design §5): icon, label, key numbers and a
 * cancel ×. Presentational, so a partial: `.r2m-job-pill` keeps the Angular element's class.
 */
export function jobPill({ job, onOpen, onCancel, tooltip }: JobPillOptions) {
  const status = JOB_STATE_STATUS[job.state];
  const summary = jobSummary(job);
  return html`<span
    class="r2m-job-pill r2m-job-pill--${status}"
    data-job=${job.id}
    data-tooltip=${tooltip || nothing}
  >
    <button
      type="button"
      class="r2m-job-pill__main"
      aria-label=${job.label}
      @click=${() => onOpen(job.id)}
    >
      ${
        status === 'busy'
          ? spinner(14, 'r2m-job-pill__spinner')
          : icon(JOB_ICONS[job.kind], 'r2m-job-pill__icon')
      }
      <span class="r2m-job-pill__label">${job.label}</span>
      ${
        summary.length
          ? html`<span class="r2m-job-pill__summary">${summary.join(' · ')}</span>`
          : nothing
      }
    </button>
    ${
      job.cancellable
        ? html`<button
            type="button"
            class="r2m-job-pill__cancel"
            aria-label="Cancel"
            @click=${(e: Event) => {
              e.stopPropagation();
              onCancel(job.id);
            }}
          >
            ${icon('close')}
          </button>`
        : nothing
    }
  </span>`;
}

const STATE_LABEL: Record<JobView['state'], string> = {
  running: 'Running',
  queued: 'Queued',
  done: 'Done',
  failed: 'Failed',
  cancelled: 'Cancelled',
};

export interface JobCardOptions {
  job: JobView;
  history?: readonly number[];
  onCancel: (id: string) => void;
  onDismiss: (id: string) => void;
}

const SPARK_WIDTH = 200;
const SPARK_HEIGHT = 32;

/** `completed / total` clamped to 1, or null when the job carries no total. */
export function jobFraction(job: JobView): number | null {
  const { completed, total } = job;
  return completed != null && total != null && total > 0 ? Math.min(1, completed / total) : null;
}

/** The dl rows of a job card, in display order. */
export function jobNumbers(j: JobView): { label: string; value: string }[] {
  const rows: { label: string; value: string }[] = [];
  if (j.queued != null) rows.push({ label: 'Queued', value: String(j.queued) });
  if (j.processing != null) rows.push({ label: 'Running', value: String(j.processing) });
  if (j.completed != null) rows.push({ label: 'Done', value: String(j.completed) });
  if (j.failed != null) rows.push({ label: 'Failed', value: String(j.failed) });
  if (j.total != null) rows.push({ label: 'Total', value: String(j.total) });
  if (j.rate) rows.push({ label: 'Rate', value: j.rate });
  if (j.etaSeconds != null) rows.push({ label: 'ETA', value: formatDuration(j.etaSeconds) });
  if (j.elapsedSeconds != null)
    rows.push({ label: 'Elapsed', value: formatDuration(j.elapsedSeconds) });
  return rows;
}

/** Expanded job status for the activity drawer (design §7): full numbers, progress, history. */
export function jobCard({ job, history, onCancel, onDismiss }: JobCardOptions) {
  const active = job.state === 'running' || job.state === 'queued';
  const fraction = jobFraction(job);
  return html`<article class="r2m-job-card" data-job=${job.id}>
    <header class="r2m-job-card__head">
      ${icon(JOB_ICONS[job.kind], 'r2m-job-card__icon')}
      <span class="r2m-job-card__label">${job.label}</span>
      ${statusChip({
        status: JOB_STATE_STATUS[job.state],
        label: STATE_LABEL[job.state],
        compact: true,
      })}
    </header>
    ${
      active
        ? fraction !== null
          ? html`<progress class="r2m-job-card__bar" max="1" value=${fraction}></progress>`
          : html`<progress class="r2m-job-card__bar"></progress>`
        : nothing
    }
    <dl class="r2m-job-card__numbers">
      ${jobNumbers(job).map(
        (n) =>
          html`<div class="r2m-job-card__number">
            <dt>${n.label}</dt>
            <dd>${n.value}</dd>
          </div>`,
      )}
    </dl>
    ${
      history?.length
        ? html`<div class="r2m-job-card__history">
            ${sparkline({
              values: history,
              width: SPARK_WIDTH,
              height: SPARK_HEIGHT,
              stroke: 'currentColor',
              label: 'Throughput history',
            })}
          </div>`
        : nothing
    }
    ${job.detail ? html`<p class="r2m-job-card__detail">${job.detail}</p>` : nothing}
    ${
      job.error
        ? html`<p class="r2m-job-card__error" role="alert">${icon('error')}${job.error}</p>`
        : nothing
    }
    <footer class="r2m-job-card__actions">
      ${
        job.cancellable
          ? html`<button
              type="button"
              class="r2m-button r2m-button--stroked"
              @click=${() => onCancel(job.id)}
            >
              Cancel
            </button>`
          : nothing
      }
      ${
        job.dismissible
          ? html`<button type="button" class="r2m-button" @click=${() => onDismiss(job.id)}>
              Dismiss
            </button>`
          : nothing
      }
    </footer>
  </article>`;
}
