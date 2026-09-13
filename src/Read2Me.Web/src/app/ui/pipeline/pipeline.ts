import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { StatusChip, StatusKind } from '../status-chip/status-chip';

export interface PipelineAction {
  id: string;
  label: string;
  icon: string;
  /** One filled button per step; the rest render as text buttons. */
  primary: boolean;
  /** Red, for actions that discard work (reread). */
  destructive?: boolean;
  disabled: boolean;
}

export interface PipelineStepView {
  id: string;
  title: string;
  icon: string;
  chip: { status: StatusKind; label: string };
  detail: string;
  /** Where the producer is: highlighted, and the marker shows the step's icon. */
  next: boolean;
  actions: PipelineAction[];
}

export interface PipelineActionEvent {
  step: string;
  action: string;
}

/**
 * Production pipeline stepper (design §6.2): one row per step with a numbered marker (a check once
 * done), title, status chip, a one-line detail and the step's actions. Presentational: the
 * overview derives the steps and handles the emitted actions. `busyAction` (`step:action`) shows a
 * spinner in that button and disables every action while it runs.
 */
@Component({
  selector: 'r2m-pipeline',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-pipeline' },
  template: `
    <ol class="r2m-pipeline__list">
      @for (step of steps(); track step.id; let i = $index; let last = $last) {
        <li
          class="r2m-pipeline__step"
          [class.r2m-pipeline__step--next]="step.next"
          [class.r2m-pipeline__step--done]="step.chip.status === 'ok'"
          [attr.data-step]="step.id"
          [attr.aria-current]="step.next ? 'step' : null"
        >
          <div class="r2m-pipeline__rail" aria-hidden="true">
            <span class="r2m-pipeline__marker">
              @if (step.chip.status === 'ok') {
                <mat-icon>check</mat-icon>
              } @else if (step.next) {
                <mat-icon>{{ step.icon }}</mat-icon>
              } @else {
                {{ i + 1 }}
              }
            </span>
            @if (!last) {
              <span class="r2m-pipeline__line"></span>
            }
          </div>
          <div class="r2m-pipeline__body">
            <div class="r2m-pipeline__head">
              <h3 class="r2m-pipeline__title">{{ step.title }}</h3>
              <r2m-status-chip
                class="r2m-pipeline__chip"
                [status]="step.chip.status"
                [label]="step.chip.label"
                compact
              />
            </div>
            <p class="r2m-pipeline__detail">{{ step.detail }}</p>
            @if (step.actions.length) {
              <div class="r2m-pipeline__actions">
                @for (action of step.actions; track action.id) {
                  @let busy = busyAction() === step.id + ':' + action.id;
                  @if (action.primary) {
                    <button
                      mat-flat-button
                      type="button"
                      class="r2m-pipeline__action r2m-pipeline__action--primary"
                      [attr.data-action]="action.id"
                      [disabled]="action.disabled || !!busyAction()"
                      (click)="act.emit({ step: step.id, action: action.id })"
                    >
                      @if (busy) {
                        <mat-progress-spinner mode="indeterminate" diameter="16" />
                      } @else {
                        <mat-icon>{{ action.icon }}</mat-icon>
                      }
                      {{ action.label }}
                    </button>
                  } @else {
                    <button
                      mat-button
                      type="button"
                      class="r2m-pipeline__action"
                      [class.r2m-pipeline__action--destructive]="action.destructive"
                      [attr.data-action]="action.id"
                      [disabled]="action.disabled || !!busyAction()"
                      (click)="act.emit({ step: step.id, action: action.id })"
                    >
                      @if (busy) {
                        <mat-progress-spinner mode="indeterminate" diameter="16" />
                      } @else {
                        <mat-icon>{{ action.icon }}</mat-icon>
                      }
                      {{ action.label }}
                    </button>
                  }
                }
              </div>
            }
          </div>
        </li>
      }
    </ol>
  `,
  styles: `
    :host {
      display: block;
    }
    .r2m-pipeline__list {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .r2m-pipeline__step {
      display: grid;
      grid-template-columns: 32px 1fr;
      column-gap: var(--r2m-space-3);
    }
    .r2m-pipeline__rail {
      display: flex;
      flex-direction: column;
      align-items: center;
    }
    .r2m-pipeline__marker {
      display: grid;
      place-items: center;
      width: 28px;
      height: 28px;
      flex: 0 0 auto;
      border-radius: 50%;
      border: 2px solid var(--r2m-outline);
      background: var(--r2m-surface);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
      font-weight: 600;
    }
    .r2m-pipeline__marker mat-icon {
      font-size: 16px;
      width: 16px;
      height: 16px;
    }
    .r2m-pipeline__step--done .r2m-pipeline__marker {
      border-color: var(--r2m-status-ok);
      background: var(--r2m-status-ok-soft);
      color: var(--r2m-status-ok);
    }
    .r2m-pipeline__step--next .r2m-pipeline__marker {
      border-color: var(--r2m-accent);
      background: var(--r2m-accent);
      color: var(--mat-sys-on-primary);
    }
    .r2m-pipeline__line {
      flex: 1 1 auto;
      width: 2px;
      min-height: var(--r2m-space-4);
      margin: var(--r2m-space-1) 0;
      background: var(--r2m-outline);
    }
    .r2m-pipeline__step--done .r2m-pipeline__line {
      background: var(--r2m-status-ok);
    }
    .r2m-pipeline__body {
      padding-bottom: var(--r2m-space-5);
      min-width: 0;
    }
    .r2m-pipeline__head {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--r2m-space-2);
      min-height: 28px;
    }
    .r2m-pipeline__title {
      margin: 0;
      font-size: var(--r2m-text-lg);
      font-weight: 600;
      color: var(--r2m-text);
    }
    .r2m-pipeline__detail {
      margin: var(--r2m-space-1) 0 0;
      font-size: var(--r2m-text-md);
      color: var(--r2m-text-muted);
    }
    .r2m-pipeline__actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--r2m-space-2);
      margin-top: var(--r2m-space-2);
    }
    .r2m-pipeline__action mat-progress-spinner {
      display: inline-block;
      margin-right: var(--r2m-space-2);
    }
    .r2m-pipeline__step:not(.r2m-pipeline__step--next) .r2m-pipeline__action--primary {
      --mat-button-filled-container-color: var(--r2m-surface-high);
      --mat-button-filled-label-text-color: var(--r2m-text);
      --mat-button-filled-icon-color: var(--r2m-text);
    }
    .r2m-pipeline__action--destructive {
      --mat-button-text-label-text-color: var(--r2m-status-error);
      --mat-button-text-icon-color: var(--r2m-status-error);
    }
  `,
})
export class Pipeline {
  readonly steps = input.required<readonly PipelineStepView[]>();
  readonly busyAction = input<string | null>(null);

  readonly act = output<PipelineActionEvent>();
}
