import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { IconName } from './icons';
import { type StatusKind, icon, spinner, statusChip } from './partials';

export interface PipelineAction {
  id: string;
  label: string;
  icon: IconName;
  /** One filled button per step; the rest render as text buttons. */
  primary: boolean;
  /** Red, for actions that discard work (reread). */
  destructive?: boolean;
  disabled: boolean;
}

export interface PipelineStepView {
  id: string;
  title: string;
  icon: IconName;
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
 * overview derives the steps and handles `onAct`. `busyAction` (`step:action`) shows a spinner in
 * that button and disables every action while it runs.
 */
export function pipeline(options: {
  steps: readonly PipelineStepView[];
  busyAction?: string | null;
  onAct: (event: PipelineActionEvent) => void;
}) {
  const { steps, onAct } = options;
  const busyAction = options.busyAction ?? null;
  const last = steps.length - 1;
  const action = (step: PipelineStepView, a: PipelineAction) => {
    const busy = busyAction === `${step.id}:${a.id}`;
    const cls = a.primary
      ? 'r2m-button r2m-button--filled r2m-pipeline__action r2m-pipeline__action--primary'
      : `r2m-button r2m-pipeline__action ${a.destructive ? 'r2m-pipeline__action--destructive' : ''}`;
    return html`<button
      type="button"
      class=${cls}
      data-action=${a.id}
      ?disabled=${a.disabled || busyAction !== null}
      @click=${() => onAct({ step: step.id, action: a.id })}
    >
      ${busy ? spinner(16, 'r2m-pipeline__spinner') : icon(a.icon)} ${a.label}
    </button>`;
  };
  return html`<ol class="r2m-pipeline">
    ${repeat(
      steps,
      (step) => step.id,
      (step, i) => {
        const done = step.chip.status === 'ok';
        return html`<li
          class="r2m-pipeline__step ${step.next ? 'r2m-pipeline__step--next' : ''} ${done ? 'r2m-pipeline__step--done' : ''}"
          data-step=${step.id}
          aria-current=${step.next ? 'step' : nothing}
        >
          <div class="r2m-pipeline__rail" aria-hidden="true">
            <span class="r2m-pipeline__marker"
              >${done ? icon('check') : step.next ? icon(step.icon) : i + 1}</span
            >
            ${i === last ? nothing : html`<span class="r2m-pipeline__line"></span>`}
          </div>
          <div class="r2m-pipeline__body">
            <div class="r2m-pipeline__head">
              <h3 class="r2m-pipeline__title">${step.title}</h3>
              <span class="r2m-pipeline__chip">${statusChip({ ...step.chip, compact: true })}</span>
            </div>
            <p class="r2m-pipeline__detail">${step.detail}</p>
            ${
              step.actions.length
                ? html`<div class="r2m-pipeline__actions">
                    ${step.actions.map((a) => action(step, a))}
                  </div>`
                : nothing
            }
          </div>
        </li>`;
      },
    )}
  </ol>`;
}
