import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltip } from '@angular/material/tooltip';
import { JOB_ICONS, JOB_STATE_STATUS, JobView, jobSummary } from './job';

/**
 * Compact one-line job status for the activity bar (design §5): icon, label, key numbers and a
 * cancel ×. The whole pill opens the activity drawer (`open`).
 */
@Component({
  selector: 'r2m-job-pill',
  imports: [MatIconModule, MatProgressSpinnerModule],
  hostDirectives: [{ directive: MatTooltip, inputs: ['matTooltip: tooltip'] }],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-job-pill',
    '[class]': '"r2m-job-pill--" + status()',
  },
  template: `
    <button
      type="button"
      class="r2m-job-pill__main"
      (click)="open.emit(job().id)"
      [attr.aria-label]="job().label"
    >
      @if (status() === 'busy') {
        <mat-progress-spinner class="r2m-job-pill__spinner" mode="indeterminate" diameter="14" />
      } @else {
        <mat-icon class="r2m-job-pill__icon" aria-hidden="true">{{ icon() }}</mat-icon>
      }
      <span class="r2m-job-pill__label">{{ job().label }}</span>
      @if (summary().length) {
        <span class="r2m-job-pill__summary">{{ summary().join(' · ') }}</span>
      }
    </button>
    @if (job().cancellable) {
      <button
        type="button"
        class="r2m-job-pill__cancel"
        (click)="cancel.emit(job().id); $event.stopPropagation()"
        aria-label="Cancel"
      >
        <mat-icon aria-hidden="true">close</mat-icon>
      </button>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      height: 26px;
      border-radius: var(--r2m-radius-pill);
      font-size: var(--r2m-text-sm);
      color: var(--_fg);
      background: var(--_bg);
      --_fg: var(--r2m-status-neutral);
      --_bg: var(--r2m-status-neutral-soft);
      white-space: nowrap;
    }
    :host(.r2m-job-pill--busy) {
      --_fg: var(--r2m-status-busy);
      --_bg: var(--r2m-status-busy-soft);
    }
    :host(.r2m-job-pill--ok) {
      --_fg: var(--r2m-status-ok);
      --_bg: var(--r2m-status-ok-soft);
    }
    :host(.r2m-job-pill--error) {
      --_fg: var(--r2m-status-error);
      --_bg: var(--r2m-status-error-soft);
    }
    .r2m-job-pill__main,
    .r2m-job-pill__cancel {
      display: inline-flex;
      align-items: center;
      gap: var(--r2m-space-1);
      height: 100%;
      border: 0;
      background: transparent;
      color: inherit;
      font: inherit;
      cursor: pointer;
      padding: 0 var(--r2m-space-2) 0 var(--r2m-space-2);
    }
    .r2m-job-pill__main:hover {
      filter: brightness(0.95);
    }
    .r2m-job-pill__label {
      font-weight: 600;
    }
    .r2m-job-pill__summary {
      color: var(--r2m-text-muted);
    }
    .r2m-job-pill__icon,
    .r2m-job-pill__cancel mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .r2m-job-pill__cancel {
      padding: 0 var(--r2m-space-1);
      border-left: 1px solid var(--r2m-outline);
    }
    .r2m-job-pill__spinner {
      --mat-progress-spinner-active-indicator-color: var(--_fg);
    }
  `,
})
export class JobPill {
  readonly job = input.required<JobView>();
  readonly open = output<string>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly cancel = output<string>();

  protected readonly status = computed(() => JOB_STATE_STATUS[this.job().state]);
  protected readonly icon = computed(() => JOB_ICONS[this.job().kind]);
  protected readonly summary = computed(() => jobSummary(this.job()));
}
