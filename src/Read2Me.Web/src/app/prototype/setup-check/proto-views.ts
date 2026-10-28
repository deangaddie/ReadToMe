/**
 * PROTOTYPE — throwaway. The four Setup check variants: the report's home, how it is surfaced at
 * start, and how the Full check's progress shows. Each variant owns its layout on purpose.
 */
import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService } from '@app/ui/dialogs';
import type { IconName } from '@app/ui/icons';
import { type StatusKind, callout, icon, spinner, statusChip } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import * as S from './proto-store';
import css from './proto.css' with { type: 'text' };

adoptStyles(css);

// ---- shared bits ------------------------------------------------------------------------------

const RESULT: Record<S.Result, { status: StatusKind; label: string; icon?: IconName }> = {
  passed: { status: 'ok', label: 'Passed' },
  failed: { status: 'error', label: 'Failed' },
  notConfirmed: { status: 'warn', label: 'Not confirmed', icon: 'question_mark' },
  notRun: { status: 'neutral', label: 'Not run', icon: 'block' },
  notUsed: { status: 'neutral', label: 'Not used', icon: 'radio_button_unchecked' },
  running: { status: 'busy', label: 'Checking…' },
};

export const resultChip = (r: S.Result, compact = false) => statusChip({ ...RESULT[r], compact });

const time = (d: Date) => d.toTimeString().slice(0, 5);

function copy(text: string) {
  void navigator.clipboard?.writeText(text);
  use(ToastService).success('Copied');
}

export function fixHint(fix: S.Fix) {
  return html`<span class="pc-fix">
    <span>${fix.text}</span>
    ${fix.href ? html`<a class="pc-fix__link" href=${fix.href}>${icon('open_in_new')} ${fix.linkLabel}</a>` : nothing}
    ${
      fix.command
        ? html`<button type="button" class="pc-fix__cmd" @click=${() => copy(fix.command!)} data-tooltip="Copy">
            <code>${fix.command}</code>${icon('content_copy')}
          </button>`
        : nothing
    }
  </span>`;
}

const GROUPS: S.Group[] = ['Workspace and tools', 'AI configs', 'Docker services'];

function summaryText(): string {
  const all = S.rows();
  const n = (r: S.Result) => all.filter((x) => x.result === r).length;
  const parts = [`${n('passed')} passed`];
  if (n('failed')) parts.unshift(`${n('failed')} failed`);
  if (n('notConfirmed')) parts.push(`${n('notConfirmed')} not confirmed`);
  if (n('notRun')) parts.push(`${n('notRun')} not run`);
  if (n('notUsed')) parts.push(`${n('notUsed')} not used`);
  return parts.join(' · ');
}

function stamp() {
  const f = S.fullAt();
  return `Quick check ${time(S.quickAt())}${f ? ` · Full check ${time(f)}` : ' · Full check not run yet'}`;
}

export async function askFull(onlyRow?: string): Promise<void> {
  if (S.noWorkspace()) return;
  const gpu = !onlyRow || ['tts', 'voice-design', 'llm', 'llm-chain', 'svc-llama', 'svc-audiocpp'].includes(onlyRow);
  if (gpu) {
    const ok = await use(ConfirmService).confirm({
      title: onlyRow ? 'Re-run this check?' : 'Run the Full check?',
      message: onlyRow
        ? 'This stops the running GPU container and loads the model this check needs, then puts things back.'
        : `This starts each service an active config uses and loads each model in turn, stopping running GPU containers on the way. Queues wait until it finishes, then everything is put back. ${S.fullEstimate}.`,
      confirmLabel: onlyRow ? 'Re-run' : 'Run Full check',
      destructive: false,
    });
    if (!ok) return;
  }
  S.startFull(onlyRow);
}

function actions(opts: { compact?: boolean } = {}) {
  const run = S.full();
  if (S.noWorkspace()) return nothing;
  return html`<span class="pc-actions">
    <button type="button" class="r2m-button r2m-button--stroked" ?disabled=${!!run} @click=${S.rerunQuick}>
      ${icon('refresh')} ${opts.compact ? 'Quick' : 'Run Quick check'}
    </button>
    ${
      run
        ? html`<button type="button" class="r2m-button r2m-button--stroked" @click=${S.cancelFull}>${icon('stop')} Cancel</button>`
        : html`<button type="button" class="r2m-button r2m-button--filled" @click=${() => void askFull()}>
            ${icon('rocket_launch')} ${opts.compact ? 'Full' : 'Run Full check'}
          </button>`
    }
  </span>`;
}

function rerunButton(r: S.Row) {
  if (r.tier !== 'full' || S.noWorkspace() || r.result === 'notUsed') return nothing;
  return html`<button
    type="button"
    class="r2m-icon-button pc-rerun"
    ?disabled=${!!S.full()}
    aria-label="Re-run"
    data-tooltip="Re-run this check (Full)"
    @click=${() => void askFull(r.id)}
  >
    ${icon('restart_alt')}
  </button>`;
}

const restoreLine = () => {
  const note = S.restoreNote();
  return note ? html`<p class="pc-restore">${icon('history')} ${note}</p>` : nothing;
};

const noWorkspaceCallout = () =>
  S.noWorkspace()
    ? callout({
        kind: 'warn',
        text: 'The app is running without a Workspace, so nothing else can load. Fix the Workspace below and restart the app; the other checks run once it has one.',
      })
    : nothing;

// ---- Variant A: Settings page, dense table, progress strip ---------------------------------------

function tableA() {
  const all = S.rows();
  return html`<table class="pc-table">
    <thead>
      <tr><th>Check</th><th>Result</th><th>Expected</th><th>Found</th><th>Fix</th><th></th></tr>
    </thead>
    ${GROUPS.map(
      (g) => html`<tbody>
        <tr class="pc-table__group"><th colspan="6">${g}</th></tr>
        ${all
          .filter((r) => r.group === g)
          .map(
            (r) => html`<tr class="pc-table__row pc-table__row--${r.result}">
              <td class="pc-table__name">${r.name}</td>
              <td>${resultChip(r.result, true)}</td>
              <td class="pc-muted">${r.expected}</td>
              <td>${r.found}${r.note ? html`<div class="pc-note">${icon('info')} ${r.note}</div>` : nothing}</td>
              <td>${r.result === 'passed' || r.result === 'notUsed' ? html`<span class="pc-muted">—</span>` : fixHint(r.fix)}</td>
              <td>${rerunButton(r)}</td>
            </tr>`,
          )}
      </tbody>`,
    )}
  </table>`;
}

function stripA() {
  const run = S.full();
  if (!run) return nothing;
  return html`<div class="pc-strip" role="status">
    ${spinner(18)}
    <span><strong>Full check ${run.step}/${run.total}</strong> · ${run.lane} · ${run.label}</span>
    <progress max=${run.total} value=${run.step - 1}></progress>
  </div>`;
}

function variantA() {
  return html`
    <header class="r2m-page-header">
      <h1 class="r2m-page-header__title">Setup check</h1>
      <span class="r2m-page-header__actions">${actions()}</span>
    </header>
    <p class="pc-muted">${summaryText()} — ${stamp()}</p>
    ${noWorkspaceCallout()} ${stripA()} ${restoreLine()} ${tableA()}
  `;
}

/** Variant A surfacing: a banner under the app bar on any start that finds a failure. */
export function bannerA() {
  if (S.variant() !== 'A' || S.noWorkspace() || S.failedCount() === 0 || S.dismissed()) return nothing;
  return html`<div class="pc-banner" role="status">
    ${icon('error')}
    <span><strong>Setup check: ${S.failedCount()} failed.</strong> Some steps will not work until they are fixed.</span>
    <a class="r2m-button r2m-button--stroked" href="settings/setup-check">Open Setup check</a>
    <button type="button" class="r2m-icon-button" aria-label="Dismiss" @click=${S.dismiss}>${icon('close')}</button>
  </div>`;
}

/** Variant A: always-on app-bar indicator. */
export function appBarA() {
  if (S.variant() !== 'A') return nothing;
  const failed = S.failedCount();
  const run = S.full();
  const status: StatusKind = run ? 'busy' : failed ? 'error' : 'ok';
  return html`<a
    class="r2m-icon-button pc-appbar pc-appbar--${status}"
    href="settings/setup-check"
    aria-label="Setup check"
    data-tooltip=${run ? `Full check ${run.step}/${run.total}` : failed ? `Setup check: ${failed} failed` : 'Setup check passed'}
    >${icon(run ? 'progress_activity' : 'health_and_safety')}${failed && !run ? html`<span class="pc-dot">${failed}</span>` : nothing}</a
  >`;
}

// ---- Variant B: Projects home panel --------------------------------------------------------------

function stepperB() {
  const run = S.full();
  if (!run) return nothing;
  const lanes: S.FullRun['lane'][] = ['Local', 'CPU', 'GPU'];
  return html`<ol class="pc-stepper">
    ${lanes.map(
      (l) => html`<li class="pc-stepper__lane ${l === run.lane ? 'pc-stepper__lane--on' : ''}">
        <span class="pc-stepper__lane-name">${l}</span>
        ${l === run.lane ? html`<span>${spinner(14)} ${run.label}</span>` : nothing}
      </li>`,
    )}
    <li class="pc-stepper__count">${run.step} of ${run.total}</li>
  </ol>`;
}

export function panelB() {
  if (S.variant() !== 'B') return nothing;
  // Collapsed: a one-line summary plus only what failed (or is being checked). Expanded on first
  // run and with no Workspace; otherwise "Show all" opens the full inventory.
  const failed = S.rows().filter((r) => r.result === 'failed' || r.result === 'running');
  const expanded = S.panelExpanded() ?? (S.firstRun() || S.noWorkspace());
  const shown = expanded ? S.rows() : failed;
  const status: StatusKind = S.full() ? 'busy' : S.failedCount() ? 'error' : 'ok';
  return html`<section class="pc-panel pc-panel--${status}" aria-label="Setup check">
    <header class="pc-panel__head">
      ${icon(status === 'error' ? 'error' : status === 'busy' ? 'progress_activity' : 'health_and_safety')}
      <div class="pc-panel__title">
        <strong>${S.firstRun() ? 'Before your first project: Setup check' : 'Setup check'}</strong>
        <span class="pc-muted">${summaryText()} · ${stamp()}</span>
      </div>
      ${actions({ compact: true })}
      <button
        type="button"
        class="r2m-icon-button"
        aria-label=${expanded ? 'Collapse' : 'Expand'}
        @click=${() => S.panelExpanded.set(!expanded)}
      >
        ${icon(expanded ? 'expand_less' : 'expand_more')}
      </button>
    </header>
    ${noWorkspaceCallout()} ${stepperB()} ${restoreLine()}
    ${
      shown.length
        ? html`<ul class="pc-panel__list">
            ${shown.map(
              (r) => html`<li class="pc-panel__row">
                ${resultChip(r.result, true)}
                <span class="pc-panel__name">${r.name}</span>
                <span class="pc-panel__found">${r.found}</span>
                ${r.result === 'failed' ? html`<span class="pc-panel__fix">${fixHint(r.fix)}</span>` : nothing}
                ${r.note ? html`<span class="pc-note">${icon('info')} ${r.note}</span>` : nothing}
                ${rerunButton(r)}
              </li>`,
            )}
          </ul>`
        : nothing
    }
    <button type="button" class="pc-panel__more" @click=${() => S.panelExpanded.set(!expanded)}>
      ${expanded ? 'Show only failures' : `Show all ${S.rows().length} checks`}
    </button>
  </section>`;
}

// ---- Variant C: top-level Setup page, one card per check, container timeline ---------------------

function timelineC() {
  const run = S.full();
  if (!run) return nothing;
  const services = ['ffmpeg', 'read2me-whisper', 'read2me-minilm-l6', 'read2me-audiocpp', 'read2me-llama'];
  const at = services.indexOf(run.service);
  return html`<div class="pc-timeline" role="status">
    ${services.map(
      (s, i) => html`<div class="pc-timeline__stop pc-timeline__stop--${i < at ? 'done' : i === at ? 'on' : 'todo'}">
        <span class="pc-timeline__dot">${i < at ? icon('check') : i === at ? spinner(16) : nothing}</span>
        <span class="pc-timeline__name">${s}</span>
      </div>`,
    )}
    <p class="pc-timeline__label">${run.label} — step ${run.step} of ${run.total}</p>
  </div>`;
}

function cardC(r: S.Row) {
  return html`<article class="pc-card pc-card--${r.result}">
    <header class="pc-card__head">
      <h3>${r.name}</h3>
      ${resultChip(r.result)} ${rerunButton(r)}
    </header>
    <dl class="pc-card__kv">
      <dt>Expected</dt><dd>${r.expected}</dd>
      <dt>Found</dt><dd>${r.found}</dd>
    </dl>
    ${r.note ? html`<p class="pc-note">${icon('info')} ${r.note}</p>` : nothing}
    ${r.result !== 'passed' && r.result !== 'notUsed' ? html`<div class="pc-card__fix">${icon('construction')} ${fixHint(r.fix)}</div>` : nothing}
  </article>`;
}

function variantC() {
  const first = S.firstRun();
  return html`
    <header class="r2m-page-header">
      <h1 class="r2m-page-header__title">${first ? 'Welcome — let’s check your setup' : 'Setup'}</h1>
      <p class="r2m-page-header__subtitle">
        ${first ? 'ReadToMe needs a few tools and AI services. Fix anything red, then start your first project.' : `${summaryText()} — ${stamp()}`}
      </p>
      <span class="r2m-page-header__actions">${actions()}</span>
    </header>
    ${noWorkspaceCallout()} ${timelineC()} ${restoreLine()}
    ${GROUPS.map(
      (g) => html`<section class="pc-section">
        <h2 class="pc-section__title">${g}</h2>
        <div class="pc-cards">${S.rows().filter((r) => r.group === g).map(cardC)}</div>
      </section>`,
    )}
    ${
      first
        ? html`<footer class="pc-continue">
            <a class="r2m-button r2m-button--filled" href="projects">Continue to Projects ${icon('arrow_forward')}</a>
          </footer>`
        : nothing
    }
  `;
}

// ---- Variant D: activity bar job + side drawer ---------------------------------------------------

export function pillD() {
  if (S.variant() !== 'D' || S.noWorkspace()) return nothing;
  const run = S.full();
  const failed = S.failedCount();
  const status: StatusKind = run ? 'busy' : failed ? 'error' : 'ok';
  const label = run ? `Full check ${run.step}/${run.total} · ${run.service}` : failed ? `Setup check · ${failed} failed` : 'Setup check passed';
  return html`<button
    type="button"
    class="pc-pill pc-pill--${status} ${failed && !S.dismissed() ? 'pc-pill--pulse' : ''}"
    @click=${() => {
      S.drawerOpen.update((o) => !o);
      S.dismiss();
    }}
  >
    ${run ? spinner(14) : icon(failed ? 'error' : 'health_and_safety')} ${label}
  </button>`;
}

function drawerBody() {
  const run = S.full();
  return html`
    <header class="pc-drawer__head">
      <h2>Setup check</h2>
      <button type="button" class="r2m-icon-button" aria-label="Close" @click=${() => S.drawerOpen.set(false)}>${icon('close')}</button>
    </header>
    <p class="pc-muted">${summaryText()}<br />${stamp()}</p>
    ${actions({ compact: true })}
    ${
      run
        ? html`<div class="pc-drawer__run">${spinner(16)} <span>${run.label}</span><progress max=${run.total} value=${run.step - 1}></progress></div>`
        : nothing
    }
    ${noWorkspaceCallout()} ${restoreLine()}
    ${GROUPS.map(
      (g) => html`<h3 class="pc-drawer__group">${g}</h3>
        <ul class="pc-drawer__list">
          ${S.rows()
            .filter((r) => r.group === g)
            .map(
              (r) => html`<li>
                <details ?open=${r.result === 'failed'}>
                  <summary>${resultChip(r.result, true)} <span>${r.name}</span> ${rerunButton(r)}</summary>
                  <div class="pc-drawer__detail">
                    <div><span class="pc-muted">Expected</span> ${r.expected}</div>
                    <div><span class="pc-muted">Found</span> ${r.found}</div>
                    ${r.note ? html`<div class="pc-note">${icon('info')} ${r.note}</div>` : nothing}
                    ${r.result !== 'passed' && r.result !== 'notUsed' ? fixHint(r.fix) : nothing}
                  </div>
                </details>
              </li>`,
            )}
        </ul>`,
    )}
  `;
}

export function drawerD() {
  if (S.variant() !== 'D' || S.noWorkspace() || !S.drawerOpen()) return nothing;
  return html`<aside class="pc-drawer" aria-label="Setup check">${drawerBody()}</aside>`;
}

// ---- the page element: whatever "the Setup check page" means for the current variant --------------

export class ProtoSetupCheckPage extends R2mElement {
  protected override connected(): void {
    this.classList.add('pc-page');
  }

  protected template() {
    const v = S.variant();
    if (S.noWorkspace()) {
      // No Workspace: every route lands here, whatever the variant.
      if (v === 'B') return html`<div class="pc-nows">${panelB()}</div>`;
      if (v === 'D') return html`<div class="pc-nows pc-nows--d">${drawerBody()}</div>`;
      return v === 'C' ? variantC() : variantA();
    }
    if (v === 'A') return variantA();
    if (v === 'C') return variantC();
    return html`<div class="pc-elsewhere">
      ${icon('info')}
      <p>
        Variant ${v} has no Setup check page: it lives
        ${v === 'B' ? html`on <a href="projects">Projects</a>` : 'in the activity bar at the bottom (click the pill)'}.
      </p>
    </div>`;
  }
}
define('r2m-proto-setup-check-page', ProtoSetupCheckPage);

/** Variant C: toast on a start with failures (not on first run: that lands on /setup itself). */
let lastToasted = 0;
export function startToastC(): void {
  if (S.variant() !== 'C' || S.firstRun() || S.noWorkspace() || S.failedCount() === 0) return;
  if (lastToasted === S.startCount()) return;
  lastToasted = S.startCount();
  use(ToastService).warn(`Setup check: ${S.failedCount()} failed — see Setup in the rail.`);
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-setup-check-page': ProtoSetupCheckPage;
  }
}
