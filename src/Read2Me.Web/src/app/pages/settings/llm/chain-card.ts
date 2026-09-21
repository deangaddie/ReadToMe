import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AttributionChainStep, toApiError } from '@app/api';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ToastService } from '@app/ui/toast/toast.service';
import { ChainOption, addStep, moveStep, optionLabel, removeStep } from './chain-steps';
import { LlmSettingsStore } from './llm-settings-store';

/**
 * The attribution escalation chain (ticket 21; Blazor's `AttributionEscalationPanel`): ordered
 * rungs with Move up / down and Remove, Add step over config × thinking × style, and the
 * self-consistency switch. Every change is one PUT of the whole chain.
 */
@Component({
  selector: 'app-llm-chain-card',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatSlideToggleModule,
    MatTooltipModule,
    StatusChip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 class="chain__title">Attribution chain</h2>
    <p class="chain__intro">
      Character attribution runs each step in order. The first answers the easy lines; suspect
      answers (unknown, unlisted name, parse failure) escalate to the next. A config can be added
      once per combination of two flags, fixed when the step is added. <b>Thinking</b> runs the step
      with model thinking on: slower, higher recall. <b>Simple</b> asks with the strict prompt,
      which names a speaker only when the text does and otherwise answers "unknown" — far less
      confabulation, more escalation. To change a step's mode, remove it and add the other variant.
    </p>

    @if (!store.chainLoaded()) {
      <p class="chain__muted">Loading…</p>
    } @else {
      @if (store.chainRows().length === 0) {
        @if (store.chainFallback(); as fallback) {
          <p class="chain__alert chain__alert--info" role="status">
            The chain is empty — attribution falls back to the default config
            <b>{{ fallback.name }}</b
            >. Add steps below to run a multi-step chain.
          </p>
        } @else {
          <p class="chain__alert chain__alert--warn" role="alert">
            The chain is empty and no default config is selected — attribution has no model to run.
          </p>
        }
      }

      <ol class="chain__steps">
        @for (row of store.chainRows(); track row.index; let first = $first, last = $last) {
          <li class="chain__step">
            <button
              mat-icon-button
              type="button"
              [disabled]="first || busy()"
              [attr.aria-label]="'Move ' + row.config.name + ' up'"
              (click)="move(row.index, -1)"
            >
              <mat-icon>arrow_upward</mat-icon>
            </button>
            <button
              mat-icon-button
              type="button"
              [disabled]="last || busy()"
              [attr.aria-label]="'Move ' + row.config.name + ' down'"
              (click)="move(row.index, 1)"
            >
              <mat-icon>arrow_downward</mat-icon>
            </button>
            <span class="chain__name">{{ row.config.name }}</span>
            @if (row.config.model) {
              <span class="chain__model">{{ row.config.model }}</span>
            }
            <span class="chain__flags">
              @if (row.simple) {
                <r2m-status-chip status="info" label="Simple" compact />
              }
              @if (row.thinking) {
                <r2m-status-chip status="info" label="Thinking" compact />
              }
            </span>
            <button
              mat-icon-button
              type="button"
              class="chain__remove"
              [disabled]="busy()"
              [attr.aria-label]="'Remove ' + row.config.name"
              matTooltip="Remove"
              (click)="remove(row.index)"
            >
              <mat-icon>delete</mat-icon>
            </button>
          </li>
        }
      </ol>

      @if (store.chainOptions().length) {
        <button
          mat-stroked-button
          type="button"
          data-action="add-step"
          [disabled]="busy()"
          [matMenuTriggerFor]="addMenu"
        >
          <mat-icon>add</mat-icon> Add step
        </button>
        <mat-menu #addMenu="matMenu">
          @for (option of store.chainOptions(); track label(option)) {
            <button mat-menu-item type="button" (click)="add(option)">{{ label(option) }}</button>
          }
        </mat-menu>
      }

      <mat-slide-toggle
        class="chain__self"
        [checked]="store.selfConsistency()"
        [disabled]="busy()"
        (change)="setSelfConsistency($event.checked)"
      >
        Self-consistency: sample twice on non-final steps and escalate on disagreement — doubles LLM
        calls for those steps
      </mat-slide-toggle>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: var(--r2m-space-4);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface);
    }
    .chain__title {
      margin: 0 0 var(--r2m-space-1);
      font-size: var(--r2m-text-lg);
      font-weight: 600;
    }
    .chain__intro,
    .chain__muted {
      margin: 0 0 var(--r2m-space-3);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .chain__alert {
      margin: 0 0 var(--r2m-space-3);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-radius: var(--r2m-radius-md);
      border-left: 3px solid var(--r2m-status-info);
      background: var(--r2m-surface-low);
    }
    .chain__alert--warn {
      border-left-color: var(--r2m-status-warn);
    }
    .chain__steps {
      list-style: none;
      margin: 0 0 var(--r2m-space-3);
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-2);
    }
    .chain__step {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-1) var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
    }
    .chain__name {
      font-weight: 500;
    }
    .chain__model {
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .chain__flags {
      display: inline-flex;
      gap: var(--r2m-space-1);
      margin-left: auto;
    }
    .chain__remove {
      color: var(--r2m-status-error);
    }
    .chain__self {
      display: block;
      margin-top: var(--r2m-space-4);
    }
  `,
})
export class LlmChainCard {
  protected readonly store = inject(LlmSettingsStore);
  private readonly toast = inject(ToastService);

  protected readonly busy = signal(false);
  protected readonly label = optionLabel;

  protected move(index: number, delta: -1 | 1): Promise<void> {
    return this.save(moveStep(this.store.steps(), index, delta));
  }

  protected remove(index: number): Promise<void> {
    return this.save(removeStep(this.store.steps(), index));
  }

  protected add(option: ChainOption): Promise<void> {
    const { config, thinking, promptStyle } = option;
    return this.save(addStep(this.store.steps(), { configId: config.id, thinking, promptStyle }));
  }

  protected setSelfConsistency(value: boolean): Promise<void> {
    return this.save(this.store.steps(), value);
  }

  private async save(
    steps: AttributionChainStep[],
    selfConsistency = this.store.selfConsistency(),
  ): Promise<void> {
    if (steps === this.store.steps() && selfConsistency === this.store.selfConsistency()) return;
    this.busy.set(true);
    try {
      await this.store.saveChain(steps, selfConsistency);
    } catch (e) {
      this.toast.problem(toApiError(e).toProblem());
    } finally {
      this.busy.set(false);
    }
  }
}
