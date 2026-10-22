import { type TemplateResult, html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import type { IconName } from './icons';
import {
  type Callout,
  type StatusKind,
  adviceLine,
  blockable,
  callout,
  icon,
  spinner,
  statusChip,
} from './partials';

export interface PipelineAction {
  id: string;
  label: string;
  icon: IconName;
  /** One filled button per step; the rest render as text buttons. */
  primary: boolean;
  /** Red, for actions that discard work (reread). */
  destructive?: boolean;
  /** Why the action is disabled, as its tooltip; null when it can be clicked. */
  blockedReason: string | null;
}

/** A checkpoint pill beside the chip; its click raises `onCheckpoint` with the pill's id. */
export interface PipelineCheckpointPill {
  id: string;
  label: string;
  /** Why the pill is off (a History step is viewed), shown as its tooltip; absent or null when on. */
  blockedReason?: string | null;
}

export interface PipelineStepView {
  id: string;
  title: string;
  icon: IconName;
  chip: { status: StatusKind; label: string };
  detail: string;
  /** Where the producer is: highlighted, and the marker shows the step's icon. */
  next: boolean;
  /** Compact outlined buttons beside the chip; empty hides the group. */
  checkpoint: PipelineCheckpointPill[];
  /** "N later steps already done" beside the pills when > 0 (the next step only). */
  laterDone: number;
  /** A state-driven banner under the detail line, or nothing. */
  callout?: Callout | null;
  /** The one advice sentence under the action group, or nothing. */
  advice?: string | null;
  actions: PipelineAction[];
}

export interface PipelineActionEvent<Id extends string = string> {
  step: Id;
  action: string;
}

export interface PipelineCheckpointEvent<Id extends string = string> {
  step: Id;
  pill: string;
}

function laterDoneHint(n: number): string {
  return `${n} later step${n === 1 ? '' : 's'} already done`;
}

/**
 * Production pipeline stepper (design §6.2, guidance-primitives spec §3 "variant B"): one row per
 * step with a numbered marker (a check once done) and a body of head row (title · chip ·
 * checkpoint pills · later-done hint · spacer · `?`), optional inline help, detail line, callout
 * slot, the step's actions each wrapped in `blockable`, and the advice line. Presentational: the
 * overview derives the steps and handles `onAct`, `onCheckpoint` and `onHelp`. `helpOpen` names
 * the one step whose `?` is on; its `renderHelp` result sits after the head row (the page's CSS
 * decides whether that placement or a side panel shows). `busyAction` (`step:action` or
 * `step:pill`) shows a spinner in that button and disables every action and pill while it runs (a
 * momentary busy carries no reason).
 */
export function pipeline<S extends PipelineStepView>(options: {
  steps: readonly S[];
  busyAction?: string | null;
  onAct: (event: PipelineActionEvent<S['id']>) => void;
  onCheckpoint?: (event: PipelineCheckpointEvent<S['id']>) => void;
  /** The step whose help is open, or null. Without `onHelp` no `?` renders. */
  helpOpen?: string | null;
  onHelp?: (step: S['id']) => void;
  renderHelp?: (step: S) => TemplateResult;
}) {
  const { steps, onAct, onCheckpoint, onHelp, renderHelp } = options;
  const busyAction = options.busyAction ?? null;
  const helpOpen = options.helpOpen ?? null;
  const last = steps.length - 1;
  const action = (step: PipelineStepView, a: PipelineAction) => {
    const busy = busyAction === `${step.id}:${a.id}`;
    const cls = [
      'r2m-button r2m-pipeline__action',
      a.primary ? 'r2m-button--filled r2m-pipeline__action--primary' : '',
      a.destructive ? 'r2m-pipeline__action--destructive' : '',
    ].join(' ');
    return blockable(
      a.blockedReason,
      html`<button
        type="button"
        class=${cls}
        data-action=${a.id}
        ?disabled=${a.blockedReason !== null || busyAction !== null}
        @click=${() => onAct({ step: step.id, action: a.id })}
      >
        ${busy ? spinner(16, 'r2m-pipeline__spinner') : icon(a.icon)} ${a.label}
      </button>`,
    );
  };
  const pill = (step: PipelineStepView, p: PipelineCheckpointPill) => {
    const blocked = p.blockedReason ?? null;
    const button = html`<button
      type="button"
      class="r2m-button r2m-button--stroked r2m-pipeline__pill"
      data-checkpoint=${p.id}
      ?disabled=${blocked !== null || busyAction !== null}
      @click=${() => onCheckpoint?.({ step: step.id, pill: p.id })}
    >
      ${busyAction === `${step.id}:${p.id}` ? spinner(14, 'r2m-pipeline__spinner') : nothing}
      ${p.label}
    </button>`;
    return blocked === null ? button : blockable(blocked, button);
  };
  const checkpointGroup = (step: PipelineStepView) =>
    step.checkpoint.length === 0 && step.laterDone === 0
      ? nothing
      : html`<span class="r2m-pipeline__checkpoint">
          ${step.checkpoint.map((p) => pill(step, p))}
          ${
            step.laterDone > 0
              ? html`<span class="r2m-pipeline__hint">${laterDoneHint(step.laterDone)}</span>`
              : nothing
          }
        </span>`;
  const helpToggle = (step: PipelineStepView, open: boolean) =>
    onHelp
      ? html`<button
          type="button"
          class="r2m-icon-button r2m-pipeline__help-toggle ${open ? 'r2m-pipeline__help-toggle--on' : ''}"
          aria-label="Help for ${step.title}"
          aria-expanded=${open ? 'true' : 'false'}
          data-tooltip="How this step works"
          @click=${() => onHelp(step.id)}
        >
          ${icon('question_mark')}
        </button>`
      : nothing;
  return html`<ol class="r2m-pipeline">
    ${repeat(
      steps,
      (step) => step.id,
      (step, i) => {
        const done = step.chip.status === 'ok';
        const open = helpOpen === step.id;
        return html`<li
          class="r2m-pipeline__step ${step.next ? 'r2m-pipeline__step--next' : ''} ${done ? 'r2m-pipeline__step--done' : ''} ${open ? 'r2m-pipeline__step--help-open' : ''}"
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
              ${checkpointGroup(step)}
              <span class="r2m-pipeline__spacer"></span>
              ${helpToggle(step, open)}
            </div>
            ${
              open && renderHelp
                ? html`<div class="r2m-pipeline__help-inline">${renderHelp(step)}</div>`
                : nothing
            }
            <p class="r2m-pipeline__detail">${step.detail}</p>
            ${step.callout ? callout(step.callout) : nothing}
            ${
              step.actions.length
                ? html`<div class="r2m-pipeline__actions">
                    ${step.actions.map((a) => action(step, a))}
                  </div>`
                : nothing
            }
            ${adviceLine(step.advice ?? null)}
          </div>
        </li>`;
      },
    )}
  </ol>`;
}
