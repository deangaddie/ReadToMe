import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { JOB_ICONS, JOB_STATE_STATUS, JobView, formatDuration } from '@app/ui/job-pill/job';

const STATE_LABEL: Record<JobView['state'], string> = {
  running: 'Running',
  queued: 'Queued',
  done: 'Done',
  failed: 'Failed',
  cancelled: 'Cancelled',
};

/** Expanded job status for the activity drawer (design §7): full numbers, progress, throughput history. */
@Component({
  selector: 'r2m-job-card',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-job-card' },
  template: `
    <header class="r2m-job-card__head">
      <mat-icon class="r2m-job-card__icon" aria-hidden="true">{{ icon() }}</mat-icon>
      <span class="r2m-job-card__label">{{ job().label }}</span>
      <r2m-status-chip [status]="status()" [label]="stateLabel()" compact />
    </header>

    @if (job().state === 'running' || job().state === 'queued') {
      @if (fraction() !== null) {
        <mat-progress-bar
          class="r2m-job-card__bar"
          mode="determinate"
          [value]="fraction()! * 100"
        />
      } @else {
        <mat-progress-bar class="r2m-job-card__bar" mode="indeterminate" />
      }
    }

    <dl class="r2m-job-card__numbers">
      @for (n of numbers(); track n.label) {
        <div class="r2m-job-card__number">
          <dt>{{ n.label }}</dt>
          <dd>{{ n.value }}</dd>
        </div>
      }
    </dl>

    @if (history()?.length) {
      <svg
        class="r2m-job-card__history"
        [attr.viewBox]="'0 0 ' + sparkWidth + ' ' + sparkHeight"
        preserveAspectRatio="none"
        role="img"
        aria-label="Throughput history"
      >
        <polyline
          [attr.points]="sparkPoints()"
          fill="none"
          stroke="currentColor"
          stroke-width="1.5"
        />
      </svg>
    }

    @if (job().detail) {
      <p class="r2m-job-card__detail">{{ job().detail }}</p>
    }
    @if (job().error) {
      <p class="r2m-job-card__error" role="alert">
        <mat-icon aria-hidden="true">error</mat-icon>{{ job().error }}
      </p>
    }

    <footer class="r2m-job-card__actions">
      @if (job().cancellable) {
        <button mat-stroked-button type="button" (click)="cancel.emit(job().id)">Cancel</button>
      }
      @if (job().dismissible) {
        <button mat-button type="button" (click)="dismiss.emit(job().id)">Dismiss</button>
      }
    </footer>
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-3) var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .r2m-job-card__head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
    }
    .r2m-job-card__icon {
      color: var(--r2m-accent);
    }
    .r2m-job-card__label {
      flex: 1;
      font-weight: 600;
    }
    .r2m-job-card__bar {
      margin: var(--r2m-space-2) 0;
    }
    .r2m-job-card__numbers {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(88px, 1fr));
      gap: var(--r2m-space-2);
      margin: var(--r2m-space-2) 0 0;
    }
    .r2m-job-card__numbers:empty {
      display: none;
    }
    .r2m-job-card__number dt {
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .r2m-job-card__number dd {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 500;
      font-variant-numeric: tabular-nums;
    }
    .r2m-job-card__history {
      display: block;
      width: 100%;
      height: 32px;
      margin-top: var(--r2m-space-2);
      color: var(--r2m-accent);
    }
    .r2m-job-card__detail {
      margin: var(--r2m-space-2) 0 0;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-job-card__error {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      margin: var(--r2m-space-2) 0 0;
      color: var(--r2m-status-error);
      font-size: var(--r2m-text-sm);
    }
    .r2m-job-card__error mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .r2m-job-card__actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--r2m-space-2);
      margin-top: var(--r2m-space-2);
    }
    .r2m-job-card__actions:empty {
      display: none;
    }
  `,
})
export class JobCard {
  readonly job = input.required<JobView>();
  readonly history = input<number[]>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly cancel = output<string>();
  readonly dismiss = output<string>();

  protected readonly sparkWidth = 200;
  protected readonly sparkHeight = 32;

  protected readonly status = computed(() => JOB_STATE_STATUS[this.job().state]);
  protected readonly stateLabel = computed(() => STATE_LABEL[this.job().state]);
  protected readonly icon = computed(() => JOB_ICONS[this.job().kind]);

  protected readonly fraction = computed(() => {
    const { completed, total } = this.job();
    return completed != null && total != null && total > 0 ? Math.min(1, completed / total) : null;
  });

  protected readonly numbers = computed(() => {
    const j = this.job();
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
  });

  protected readonly sparkPoints = computed(() => {
    const values = this.history() ?? [];
    if (values.length === 0) return '';
    const min = Math.min(...values);
    const max = Math.max(...values);
    const span = max - min || 1;
    const stepX = values.length > 1 ? this.sparkWidth / (values.length - 1) : 0;
    return values
      .map(
        (v, i) =>
          `${(i * stepX).toFixed(1)},${(this.sparkHeight - ((v - min) / span) * (this.sparkHeight - 2) - 1).toFixed(1)}`,
      )
      .join(' ');
  });
}
