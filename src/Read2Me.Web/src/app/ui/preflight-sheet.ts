import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { type AiTaskKind, PreflightApi, type PreflightPlanDto, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import type { PreflightMessage, PreflightStage } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { SERVICE_STATUS_VIEW } from './docker-controls';
import type { IconName } from './icons';
import { type StatusKind, icon, spinner, statusChip } from './partials';
import { ToastService } from './toast';

export type PreflightPlan = PreflightPlanDto;

export interface PreflightServiceProgress {
  name: string;
  stage: PreflightStage;
  error?: string | null;
}

export type PreflightPhase = 'plan' | 'running' | 'done' | 'failed';
const PHASES: readonly PreflightPhase[] = ['plan', 'running', 'done', 'failed'];

export interface PreflightSheetData {
  kind: AiTaskKind;
  label: string;
  plan: PreflightPlanDto;
}

const STAGE_VIEW: Record<PreflightStage, { label: string; kind: StatusKind; active: boolean }> = {
  waitingToStop: { label: 'Waiting to stop', kind: 'neutral', active: false },
  stopping: { label: 'Stopping', kind: 'busy', active: true },
  stopped: { label: 'Stopped', kind: 'neutral', active: false },
  waitingToStart: { label: 'Waiting to start', kind: 'neutral', active: false },
  starting: { label: 'Starting', kind: 'busy', active: true },
  ready: { label: 'Ready', kind: 'ok', active: false },
  failed: { label: 'Failed', kind: 'error', active: false },
};

const STAGE_ICON: Partial<Record<PreflightStage, IconName>> = {
  ready: 'check_circle',
  failed: 'error',
  stopped: 'stop_circle',
};

const HEAD_ICON: Record<PreflightPhase, IconName> = {
  plan: 'rocket_launch',
  running: 'hourglass_top',
  done: 'check_circle',
  failed: 'error',
};

/** The rows the run will walk, in the order the host walks them: conflicts stopped first. */
export function initialProgress(plan: PreflightPlanDto): PreflightServiceProgress[] {
  return [
    ...plan.conflicts.map((c) => ({ name: c.name, stage: 'waitingToStop' as const })),
    ...plan.toStart.map((s) => ({ name: s.name, stage: 'waitingToStart' as const })),
  ];
}

/**
 * Readiness sheet shown before every AI action (design principle 4, §7), opened by
 * `Preflight.ensureReady` in a bottom-docked dialog. Angular split this into a presentational
 * component and a bottom-sheet host; here one element owns both: the plan, the run (`/run`,
 * progress over the hub) and the result it closes with — `r2m-close` carries `true` once the host
 * reports the run done and ok, `false` for cancel or failure.
 */
export class PreflightSheet extends R2mElement {
  #data = signal<PreflightSheetData | null>(null);
  /** The task and its plan; set before the sheet is appended. */
  get data(): PreflightSheetData {
    const data = this.#data();
    if (!data) throw new Error('r2m-preflight-sheet needs its data before it renders');
    return data;
  }
  set data(value: PreflightSheetData) {
    this.#data.set(value);
  }

  private readonly api = use(PreflightApi);
  private readonly live = use(LiveService);
  private readonly toast = use(ToastService);

  readonly phase = signal<PreflightPhase>('plan');
  readonly progress = signal<PreflightServiceProgress[]>([]);
  readonly error = signal<string | null>(null);

  private readonly failedServices = computed(() =>
    this.progress().filter((p) => p.stage === 'failed'),
  );

  /** The run this sheet follows; messages that arrive before the 202 answers wait here. */
  private run: string | null = null;
  private early: PreflightMessage[] = [];

  protected override connected(): void {
    this.classList.add('r2m-preflight-sheet');
    this.progress.set(initialProgress(this.data.plan));
    this.onDisconnect(
      this.live.on('preflight', (m) => {
        if (this.run === null) this.early.push(m);
        else if (m.run === this.run) this.apply(m);
      }),
    );
  }

  protected template() {
    const phase = this.phase();
    // The phase modifier colours the header; the host class set on connect stays.
    for (const p of PHASES) this.classList.toggle(`r2m-preflight-sheet--${p}`, p === phase);
    return html`
      <header class="r2m-preflight-sheet__head">
        ${icon(HEAD_ICON[phase])}
        <h2 class="r2m-preflight-sheet__title">${this.headline(phase)}</h2>
      </header>
      ${this.body(phase)}
    `;
  }

  private headline(phase: PreflightPhase): string {
    switch (phase) {
      case 'plan':
        return `${this.data.label} needs AI services`;
      case 'running':
        return 'Starting services';
      case 'done':
        return 'Ready';
      default:
        return 'Services could not be started';
    }
  }

  private body(phase: PreflightPhase) {
    switch (phase) {
      case 'plan':
        return this.planBody();
      case 'running':
        return html`
          <ul class="r2m-preflight-sheet__list">
            ${repeat(
              this.progress(),
              (p) => p.name,
              (p) => this.progressRow(p),
            )}
          </ul>
          <footer class="r2m-preflight-sheet__actions">
            <button type="button" class="r2m-button" @click=${this.cancel}>Cancel</button>
          </footer>
        `;
      case 'done':
        return html`<p class="r2m-preflight-sheet__muted">
          All required services are ready. Starting ${this.data.label}…
        </p>`;
      case 'failed':
        return html`
          <p class="r2m-preflight-sheet__error" role="alert">
            ${this.error() || 'One or more services failed to start.'}
          </p>
          ${
            this.failedServices().length
              ? html`<ul class="r2m-preflight-sheet__list">
                  ${this.failedServices().map(
                    (p) =>
                      html`<li class="r2m-preflight-sheet__row">
                        ${icon('error')}
                        <span class="r2m-preflight-sheet__name">${p.name}</span>
                        <span class="r2m-preflight-sheet__reason">${p.error}</span>
                      </li>`,
                  )}
                </ul>`
              : nothing
          }
          <footer class="r2m-preflight-sheet__actions">
            <button
              type="button"
              class="r2m-button r2m-button--filled"
              @click=${() => this.emit('r2m-close', false)}
            >
              Close
            </button>
          </footer>
        `;
    }
  }

  private planBody() {
    const p = this.data.plan;
    return html`
      ${
        p.conflicts.length
          ? html`<section class="r2m-preflight-sheet__section">
              <h3>Will be stopped first</h3>
              <ul class="r2m-preflight-sheet__list">
                ${p.conflicts.map(
                  (c) =>
                    html`<li class="r2m-preflight-sheet__row r2m-preflight-sheet__row--conflict">
                      ${icon('gpp_maybe')}
                      <span class="r2m-preflight-sheet__name">${c.name}</span>
                      <span class="r2m-preflight-sheet__reason">${c.reason}</span>
                    </li>`,
                )}
              </ul>
            </section>`
          : nothing
      }
      <section class="r2m-preflight-sheet__section">
        <h3>Services to start</h3>
        ${
          p.toStart.length
            ? html`<ul class="r2m-preflight-sheet__list">
                ${p.toStart.map(
                  (s) =>
                    html`<li class="r2m-preflight-sheet__row">
                      ${icon('dns')}
                      <span class="r2m-preflight-sheet__name">${s.name}</span>
                      ${statusChip({
                        status: SERVICE_STATUS_VIEW[s.status].kind,
                        label: s.status,
                        compact: true,
                      })}
                    </li>`,
                )}
              </ul>`
            : html`<p class="r2m-preflight-sheet__muted">Nothing to start.</p>`
        }
      </section>
      <footer class="r2m-preflight-sheet__actions">
        <button type="button" class="r2m-button" @click=${this.cancel}>Cancel</button>
        <button type="button" class="r2m-button r2m-button--filled" autofocus @click=${this.start}>
          Start services
        </button>
      </footer>
    `;
  }

  private progressRow(p: PreflightServiceProgress) {
    const view = STAGE_VIEW[p.stage];
    return html`<li class="r2m-preflight-sheet__row" data-stage=${p.stage}>
      ${view.active ? spinner(18) : icon(STAGE_ICON[p.stage] ?? 'schedule')}
      <span class="r2m-preflight-sheet__name">${p.name}</span>
      ${statusChip({ status: view.kind, label: view.label, compact: true })}
      ${p.error ? html`<span class="r2m-preflight-sheet__reason">${p.error}</span>` : nothing}
    </li>`;
  }

  private readonly start = async (): Promise<void> => {
    const connectionId = this.live.connectionId();
    if (connectionId === null) {
      this.toast.warn('Live updates are disconnected — try again in a moment');
      return;
    }
    this.phase.set('running');
    try {
      const { run } = await this.api.run(this.data.kind, { connectionId });
      this.run = run;
      const early = this.early;
      this.early = [];
      for (const m of early) if (m.run === run) this.apply(m);
    } catch (e) {
      this.phase.set('failed');
      this.error.set(toApiError(e).message);
    }
  };

  private readonly cancel = (): void => {
    if (this.phase() === 'running') {
      // Docker ops are not safely abortable: the host finishes them; only this task is not started.
      this.toast.info('Services continue starting in the background');
    }
    this.emit('r2m-close', false);
  };

  private apply(m: PreflightMessage): void {
    if (m.kind === 'stage' && m.name && m.stage) {
      const name = m.name;
      const stage = m.stage;
      const error = m.error ?? null;
      this.progress.update((rows) =>
        rows.some((r) => r.name === name)
          ? rows.map((r) => (r.name === name ? { ...r, stage, error } : r))
          : [...rows, { name, stage, error }],
      );
      return;
    }
    if (m.kind === 'done') {
      if (m.ok) {
        this.phase.set('done');
        this.emit('r2m-close', true);
      } else {
        this.phase.set('failed');
        this.error.set(m.reason ?? null);
      }
    }
  }
}
define('r2m-preflight-sheet', PreflightSheet);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-preflight-sheet': PreflightSheet;
  }
}
