import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { StatusChip, StatusKind } from '@app/ui/status-chip/status-chip';

export interface PreflightPlan {
  ready: boolean;
  toStart: { name: string; status: string }[];
  conflicts: { name: string; reason: string }[];
}

export type PreflightStage =
  'waitingToStop' | 'stopping' | 'stopped' | 'waitingToStart' | 'starting' | 'ready' | 'failed';

export interface PreflightServiceProgress {
  name: string;
  stage: PreflightStage;
  error?: string | null;
}

export type PreflightPhase = 'plan' | 'running' | 'done' | 'failed';

const STAGE_VIEW: Record<PreflightStage, { label: string; kind: StatusKind; active: boolean }> = {
  waitingToStop: { label: 'Waiting to stop', kind: 'neutral', active: false },
  stopping: { label: 'Stopping', kind: 'busy', active: true },
  stopped: { label: 'Stopped', kind: 'neutral', active: false },
  waitingToStart: { label: 'Waiting to start', kind: 'neutral', active: false },
  starting: { label: 'Starting', kind: 'busy', active: true },
  ready: { label: 'Ready', kind: 'ok', active: false },
  failed: { label: 'Failed', kind: 'error', active: false },
};

/** Status chip kind for a plan row's container status string (`AiServiceStatus` member name). */
function statusKind(status: string): StatusKind {
  switch (status) {
    case 'Ready':
      return 'ok';
    case 'Starting':
      return 'busy';
    case 'NotFound':
      return 'warn';
    default:
      return 'neutral';
  }
}

/**
 * Readiness sheet shown before every AI action (design principle 4, §7). Presentational: the
 * bottom-sheet wrapper, plan/run calls and hub-fed progress live in `PreflightSheetHost`
 * (`app/shared/preflight-sheet-host.ts`), opened by `Preflight.ensureReady`.
 */
@Component({
  selector: 'r2m-preflight-sheet',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-preflight-sheet', '[class]': '"r2m-preflight-sheet--" + phase()' },
  template: `
    <header class="r2m-preflight-sheet__head">
      <mat-icon aria-hidden="true">{{ headIcon() }}</mat-icon>
      <h2 class="r2m-preflight-sheet__title">{{ headline() }}</h2>
    </header>

    @switch (phase()) {
      @case ('plan') {
        @if (plan(); as p) {
          @if (p.conflicts.length) {
            <section class="r2m-preflight-sheet__section">
              <h3>Will be stopped first</h3>
              <ul class="r2m-preflight-sheet__list">
                @for (c of p.conflicts; track c.name) {
                  <li class="r2m-preflight-sheet__row r2m-preflight-sheet__row--conflict">
                    <mat-icon aria-hidden="true">gpp_maybe</mat-icon>
                    <span class="r2m-preflight-sheet__name">{{ c.name }}</span>
                    <span class="r2m-preflight-sheet__reason">{{ c.reason }}</span>
                  </li>
                }
              </ul>
            </section>
          }
          <section class="r2m-preflight-sheet__section">
            <h3>Services to start</h3>
            @if (p.toStart.length) {
              <ul class="r2m-preflight-sheet__list">
                @for (s of p.toStart; track s.name) {
                  <li class="r2m-preflight-sheet__row">
                    <mat-icon aria-hidden="true">dns</mat-icon>
                    <span class="r2m-preflight-sheet__name">{{ s.name }}</span>
                    <r2m-status-chip [status]="planStatus(s.status)" [label]="s.status" compact />
                  </li>
                }
              </ul>
            } @else {
              <p class="r2m-preflight-sheet__muted">Nothing to start.</p>
            }
          </section>
        }
        <footer class="r2m-preflight-sheet__actions">
          <button mat-button type="button" (click)="cancel.emit()">Cancel</button>
          <button mat-flat-button type="button" (click)="start.emit()">Start services</button>
        </footer>
      }
      @case ('running') {
        <ul class="r2m-preflight-sheet__list">
          @for (p of progress(); track p.name) {
            <li class="r2m-preflight-sheet__row" [attr.data-stage]="p.stage">
              @if (stage(p).active) {
                <mat-progress-spinner mode="indeterminate" diameter="18" />
              } @else {
                <mat-icon aria-hidden="true">{{ stageIcon(p.stage) }}</mat-icon>
              }
              <span class="r2m-preflight-sheet__name">{{ p.name }}</span>
              <r2m-status-chip [status]="stage(p).kind" [label]="stage(p).label" compact />
              @if (p.error) {
                <span class="r2m-preflight-sheet__reason">{{ p.error }}</span>
              }
            </li>
          }
        </ul>
        <footer class="r2m-preflight-sheet__actions">
          <button mat-button type="button" (click)="cancel.emit()">Cancel</button>
        </footer>
      }
      @case ('done') {
        <p class="r2m-preflight-sheet__muted">
          All required services are ready. Starting {{ taskLabel() }}…
        </p>
      }
      @case ('failed') {
        <p class="r2m-preflight-sheet__error" role="alert">
          {{ error() || 'One or more services failed to start.' }}
        </p>
        @if (failedServices().length) {
          <ul class="r2m-preflight-sheet__list">
            @for (p of failedServices(); track p.name) {
              <li class="r2m-preflight-sheet__row">
                <mat-icon aria-hidden="true">error</mat-icon>
                <span class="r2m-preflight-sheet__name">{{ p.name }}</span>
                <span class="r2m-preflight-sheet__reason">{{ p.error }}</span>
              </li>
            }
          </ul>
        }
        <footer class="r2m-preflight-sheet__actions">
          <button mat-flat-button type="button" (click)="close.emit()">Close</button>
        </footer>
      }
    }
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-4);
      min-width: min(480px, 100vw);
    }
    .r2m-preflight-sheet__head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      margin-bottom: var(--r2m-space-3);
      color: var(--r2m-accent);
    }
    :host(.r2m-preflight-sheet--failed) .r2m-preflight-sheet__head {
      color: var(--r2m-status-error);
    }
    :host(.r2m-preflight-sheet--done) .r2m-preflight-sheet__head {
      color: var(--r2m-status-ok);
    }
    .r2m-preflight-sheet__title {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 600;
      color: var(--r2m-text);
    }
    .r2m-preflight-sheet__section h3 {
      margin: var(--r2m-space-2) 0 var(--r2m-space-1);
      font-size: var(--r2m-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--r2m-text-muted);
    }
    .r2m-preflight-sheet__list {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .r2m-preflight-sheet__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      min-height: 36px;
    }
    .r2m-preflight-sheet__row mat-icon {
      color: var(--r2m-text-muted);
    }
    .r2m-preflight-sheet__row--conflict mat-icon,
    :host(.r2m-preflight-sheet--failed) .r2m-preflight-sheet__row mat-icon {
      color: var(--r2m-status-warn);
    }
    :host(.r2m-preflight-sheet--failed) .r2m-preflight-sheet__row mat-icon {
      color: var(--r2m-status-error);
    }
    .r2m-preflight-sheet__name {
      font-weight: 500;
    }
    .r2m-preflight-sheet__reason,
    .r2m-preflight-sheet__muted {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-preflight-sheet__error {
      margin: 0 0 var(--r2m-space-2);
      color: var(--r2m-status-error);
    }
    .r2m-preflight-sheet__actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--r2m-space-2);
      margin-top: var(--r2m-space-4);
    }
  `,
})
export class PreflightSheet {
  readonly taskLabel = input.required<string>();
  readonly phase = input<PreflightPhase>('plan');
  readonly plan = input<PreflightPlan | null>(null);
  readonly progress = input<PreflightServiceProgress[]>([]);
  readonly error = input<string | null>();

  readonly start = output<void>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly cancel = output<void>();
  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly close = output<void>();

  protected readonly headline = computed(() => {
    switch (this.phase()) {
      case 'plan':
        return `${this.taskLabel()} needs AI services`;
      case 'running':
        return 'Starting services';
      case 'done':
        return 'Ready';
      default:
        return 'Services could not be started';
    }
  });

  protected readonly headIcon = computed(() => {
    switch (this.phase()) {
      case 'plan':
        return 'rocket_launch';
      case 'running':
        return 'hourglass_top';
      case 'done':
        return 'check_circle';
      default:
        return 'error';
    }
  });

  protected readonly failedServices = computed(() =>
    this.progress().filter((p) => p.stage === 'failed'),
  );

  protected planStatus(status: string): StatusKind {
    return statusKind(status);
  }

  protected stage(p: PreflightServiceProgress) {
    return STAGE_VIEW[p.stage];
  }

  protected stageIcon(stage: PreflightStage): string {
    switch (stage) {
      case 'ready':
        return 'check_circle';
      case 'failed':
        return 'error';
      case 'stopped':
        return 'stop_circle';
      default:
        return 'schedule';
    }
  }
}
