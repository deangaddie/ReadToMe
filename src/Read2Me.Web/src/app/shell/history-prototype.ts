/**
 * PROTOTYPE — throwaway. Three variants of project History, switchable via `?variant=A|B|C` on any
 * project route (the shell hosts them). Answers `.scratch/project-history/issues/06-history-panel-prototype.md`:
 * where History lives, what a step row shows, how "viewing a past step, read-only" reads across the
 * whole app, where Restore sits and whether it confirms, and how discarded audio is reported.
 * Fake steps; Restore only appends to an in-memory list. Delete with the branch.
 */
import { html, nothing } from 'lit-html';
import { signal } from '@app/core/signals';
import type { IconName } from '@app/ui/icons';
import { icon } from '@app/ui/partials';

export const VARIANTS = ['A', 'B', 'C'] as const;
export type Variant = (typeof VARIANTS)[number];
export const VARIANT_NAMES: Record<Variant, string> = {
  A: 'Side sheet · banner while viewing · Restore with Undo, no confirm',
  B: 'History page · detail pane · tinted app bar · Restore confirms',
  C: 'Timeline strip · card on the tick · inline confirm',
};

interface Step {
  seq: number;
  label: string;
  touched: string;
  icon: IconName;
  day: string;
  time: string;
  author: string;
  /** Background run part, interrupted run, restore… */
  note?: string;
  restoredFrom?: number;
  run?: boolean;
  /** Items whose audio a Restore to *before* this step would discard (fake). */
  audioCost: number;
}

const seed: Step[] = [
  { seq: 1, label: 'Import', touched: 'dune.epub · 3 volumes, 48 chapters, 14,895 items', icon: 'upload_file', day: 'Yesterday', time: '09:12', author: 'Dean', audioCost: 0 },
  { seq: 2, label: 'Add titles', touched: 'Chapter titles on 48 chapters', icon: 'title', day: 'Yesterday', time: '09:20', author: 'Dean', audioCost: 0 },
  { seq: 3, label: 'Attribution', touched: 'Volume 1 · 2,104 paragraphs · 31 characters created', icon: 'smart_toy', day: 'Yesterday', time: '09:24–11:02', author: 'Dean', run: true, note: 'part 1', audioCost: 0 },
  { seq: 4, label: 'Rename character', touched: '“Paul” → “Paul Atreides”', icon: 'edit', day: 'Yesterday', time: '10:15', author: 'Dean', audioCost: 0 },
  { seq: 5, label: 'Attribution', touched: 'Volume 1 · 1,388 paragraphs', icon: 'smart_toy', day: 'Yesterday', time: '10:15–11:40', author: 'Dean', run: true, note: 'part 2', audioCost: 0 },
  { seq: 6, label: 'Voice batch', touched: 'Prompts + audio for 31 characters', icon: 'record_voice_over', day: 'Yesterday', time: '13:05–13:48', author: 'Dean', run: true, audioCost: 0 },
  { seq: 7, label: 'Merge characters', touched: '“The Baron” into “Baron Harkonnen”', icon: 'merge', day: 'Yesterday', time: '14:02', author: 'Dean', audioCost: 212 },
  { seq: 8, label: 'Split chapter', touched: 'Chapter 3 at paragraph 41', icon: 'call_split', day: 'Today', time: '08:31', author: 'Dean', audioCost: 0 },
  { seq: 9, label: 'Edit with AI', touched: '4 paragraphs in Chapter 3', icon: 'auto_awesome', day: 'Today', time: '08:44', author: 'Dean', audioCost: 4 },
  { seq: 10, label: 'Attribution', touched: 'Volume 2 · 212 of 340 paragraphs', icon: 'smart_toy', day: 'Today', time: '09:02–09:30', author: 'Dean', run: true, note: 'cancelled', audioCost: 37 },
  { seq: 11, label: 'Edit text', touched: 'Chapter 5 · paragraph 12', icon: 'edit', day: 'Today', time: '09:41', author: 'Dean', audioCost: 1 },
  { seq: 12, label: 'Assign speaker', touched: '18 paragraphs → Gurney Halleck', icon: 'person', day: 'Today', time: '09:55', author: 'Dean', audioCost: 18 },
];

export const steps = signal<Step[]>(seed);
/** The step being viewed read-only; null = current. */
export const viewing = signal<number | null>(null);
export const historyOpen = signal(false);
export const toast = signal<{ text: string; undo: number | undefined } | null>(null);
const confirming = signal<number | null>(null);
const selected = signal<number | null>(null);

const tip = () => steps().at(-1)!;
const bySeq = (seq: number) => steps().find((s) => s.seq === seq)!;

/** Items that lose audio restoring to `seq` (fake: every later step's cost). */
const cost = (seq: number) => steps().filter((s) => s.seq > seq).reduce((n, s) => n + s.audioCost, 0);

function restore(seq: number, withUndo: boolean) {
  const from = bySeq(seq);
  const lost = cost(seq);
  const prevTip = tip().seq;
  const n = prevTip + 1;
  steps.update((list) => [
    ...list,
    {
      seq: n,
      label: 'Restore',
      touched: `to #${seq} ${from.label} (${from.time})`,
      icon: 'restart_alt',
      day: 'Today',
      time: '10:07',
      author: 'Dean',
      restoredFrom: seq,
      audioCost: 0,
    },
  ]);
  viewing.set(null);
  confirming.set(null);
  selected.set(null);
  toast.set({
    text: `Restored to #${seq} ${from.label}. Steps #${seq + 1}–#${prevTip} stay in History.${
      lost ? ` ${lost} items lost their audio and need generating.` : ''
    }`,
    undo: withUndo ? prevTip : undefined,
  });
}

const stepTitle = (s: Step) =>
  html`<span class="proto-h__label">${s.label}${s.note ? html` <em>(${s.note})</em>` : nothing}</span>`;

function stepIcon(s: Step) {
  return html`<span class="proto-h__icon ${s.restoredFrom ? 'proto-h__icon--restore' : ''} ${s.run ? 'proto-h__icon--run' : ''}"
    >${icon(s.icon)}</span
  >`;
}

/* ---- app bar button (A, B) -------------------------------------------------------------------- */

export function appBarButton(variant: Variant) {
  if (variant === 'C') return nothing;
  return html`<button
    type="button"
    class="r2m-icon-button ${historyOpen() ? 'proto-h__bar-on' : ''}"
    aria-label="History"
    data-tooltip="History"
    @click=${() => historyOpen.update((o) => !o)}
  >
    ${icon('history')}
  </button>`;
}

/** B tints the app bar while viewing; returns the chip shown there. */
export function appBarViewing(variant: Variant) {
  const v = viewing();
  if (variant !== 'B' || v === null) return nothing;
  const s = bySeq(v);
  return html`<span class="proto-h__barchip">
    ${icon('lock')} Viewing #${s.seq} ${s.label} · ${s.day} ${s.time} · read-only
    <button type="button" class="r2m-button r2m-button--stroked" @click=${() => viewing.set(null)}>
      Back to current
    </button>
  </span>`;
}

/* ---- what renders around / instead of <main> ---------------------------------------------------- */

/** True when the variant replaces the page outlet (B's History page). */
export const replacesMain = (variant: Variant) => variant === 'B' && historyOpen() && viewing() === null;

export function beforeMain(variant: Variant) {
  const v = viewing();
  if (variant !== 'A' || v === null) return nothing;
  const s = bySeq(v);
  return html`<div class="proto-h__banner" role="status">
    ${icon('lock')}
    <span
      ><b>Viewing #${s.seq} ${s.label}</b> — ${s.touched} · ${s.day} ${s.time}. The project as it was after this
      step; read-only.</span
    >
    <span class="shell__spacer"></span>
    <button type="button" class="r2m-button r2m-button--filled" @click=${() => restore(s.seq, true)}>
      Restore this step
    </button>
    <button type="button" class="r2m-button r2m-button--stroked" @click=${() => viewing.set(null)}>
      Back to current
    </button>
  </div>`;
}

/** Between the shell body and the activity footer: the toast is fixed; C's strip docks here. */
export function afterBody(variant: Variant) {
  return html`${variant === 'C' ? stripC() : nothing} ${toastView()}`;
}

/** Docked beside <main> inside the shell body (A's sheet). */
export function besideMain(variant: Variant) {
  return variant === 'A' ? sheetA() : nothing;
}

/** Inside <main>, in place of the hidden outlet. */
export function mainReplacement(variant: Variant) {
  return replacesMain(variant) ? pageB() : nothing;
}

/* ---- A: side sheet ------------------------------------------------------------------------------- */

function sheetA() {
  if (!historyOpen()) return nothing;
  const list = [...steps()].reverse();
  const days = [...new Set(list.map((s) => s.day))];
  const v = viewing();
  return html`<aside class="proto-h__sheet" aria-label="History">
    <header class="proto-h__sheet-head">
      <h2>History</h2>
      <span class="shell__spacer"></span>
      <button type="button" class="r2m-icon-button" aria-label="Close" @click=${() => historyOpen.set(false)}>
        ${icon('close')}
      </button>
    </header>
    <p class="r2m-advice">Click a step to see the whole project as it was after it. Restoring adds a new step; nothing is lost.</p>
    ${days.map(
      (day) => html`<h3 class="proto-h__day">${day}</h3>
        <ol class="proto-h__list">
          ${list
            .filter((s) => s.day === day)
            .map(
              (s) => html`<li>
                <button
                  type="button"
                  class="proto-h__row ${v === s.seq ? 'proto-h__row--on' : ''} ${s.seq === tip().seq ? 'proto-h__row--tip' : ''}"
                  @click=${() => viewing.set(s.seq === tip().seq ? null : s.seq)}
                >
                  ${stepIcon(s)}
                  <span class="proto-h__text">${stepTitle(s)}<span class="proto-h__touched">${s.touched}</span></span>
                  <span class="proto-h__time">${s.seq === tip().seq ? 'Current' : s.time}</span>
                </button>
              </li>`,
            )}
        </ol>`,
    )}
  </aside>`;
}

/* ---- B: History page with a detail pane ---------------------------------------------------------- */

function pageB() {
  const list = [...steps()].reverse();
  const sel = bySeq(selected() ?? tip().seq);
  const isTip = sel.seq === tip().seq;
  return html`<section class="proto-h__page" aria-label="History">
    <header class="r2m-page-header"><h1 class="r2m-page-header__title">History</h1></header>
    <div class="proto-h__page-body">
      <table class="proto-h__table">
        <thead>
          <tr><th>#</th><th>Step</th><th>What changed</th><th>When</th><th>By</th></tr>
        </thead>
        <tbody>
          ${list.map(
            (s) => html`<tr
              class="${sel.seq === s.seq ? 'proto-h__tr--on' : ''}"
              @click=${() => selected.set(s.seq)}
            >
              <td>${s.seq}</td>
              <td>${stepIcon(s)} ${stepTitle(s)}</td>
              <td class="proto-h__touched">${s.touched}</td>
              <td>${s.day} ${s.time}</td>
              <td>${s.author}</td>
            </tr>`,
          )}
        </tbody>
      </table>
      <aside class="proto-h__detail">
        <h2>#${sel.seq} ${sel.label}${sel.note ? ` (${sel.note})` : ''}</h2>
        <p class="r2m-advice">${sel.day} ${sel.time} · ${sel.author}</p>
        <h3>What changed</h3>
        <ul>
          <li>${sel.touched}</li>
          ${sel.restoredFrom ? html`<li>Same state as #${sel.restoredFrom}</li>` : nothing}
          ${sel.run ? html`<li>Background run — split where you made changes</li>` : nothing}
        </ul>
        ${
          isTip
            ? html`<p class="r2m-advice">This is the current state.</p>`
            : html`<div class="proto-h__actions">
                <button type="button" class="r2m-button r2m-button--stroked" @click=${() => viewing.set(sel.seq)}>
                  ${icon('lock')} Open read-only
                </button>
                <button type="button" class="r2m-button r2m-button--filled" @click=${() => confirming.set(sel.seq)}>
                  ${icon('restart_alt')} Restore…
                </button>
              </div>`
        }
        ${confirming() === sel.seq ? confirmDialogB(sel) : nothing}
      </aside>
    </div>
  </section>`;
}

function confirmDialogB(s: Step) {
  const lost = cost(s.seq);
  return html`<div class="proto-h__dialog" role="dialog" aria-label="Restore">
    <h3>Restore #${s.seq} ${s.label}?</h3>
    <p>The project goes back to how it was after this step. This adds a new step #${tip().seq + 1}; steps
      #${s.seq + 1}–#${tip().seq} stay in History and can be restored again.</p>
    ${lost ? html`<div class="r2m-callout r2m-callout--warn"><span class="r2m-callout__text">${lost} items will lose their audio and need generating.</span></div>` : nothing}
    <div class="proto-h__actions">
      <button type="button" class="r2m-button r2m-button--stroked" @click=${() => confirming.set(null)}>Cancel</button>
      <button type="button" class="r2m-button r2m-button--filled" @click=${() => { restore(s.seq, false); historyOpen.set(false); }}>
        Restore
      </button>
    </div>
  </div>`;
}

/* ---- C: timeline strip --------------------------------------------------------------------------- */

function stripC() {
  const v = viewing();
  const list = steps();
  const card = v !== null ? bySeq(v) : null;
  return html`<div class="proto-h__strip" aria-label="History timeline">
    ${
      card
        ? html`<div class="proto-h__card" role="status">
            <div class="proto-h__card-head">${stepIcon(card)} <b>#${card.seq} ${stepTitle(card)}</b></div>
            <div class="proto-h__touched">${card.touched} · ${card.day} ${card.time}</div>
            <div class="proto-h__touched">${icon('lock')} You're seeing the project as it was here; read-only.</div>
            ${
              confirming() === card.seq
                ? html`<div class="r2m-callout r2m-callout--warn">
                    <span class="r2m-callout__text"
                      >Adds step #${tip().seq + 1}. Later steps stay.${cost(card.seq) ? ` ${cost(card.seq)} items will need audio.` : ''}</span
                    >
                    <button type="button" class="r2m-button r2m-button--filled" @click=${() => restore(card.seq, false)}>Confirm</button>
                    <button type="button" class="r2m-button r2m-button--stroked" @click=${() => confirming.set(null)}>Cancel</button>
                  </div>`
                : html`<div class="proto-h__actions">
                    <button type="button" class="r2m-button r2m-button--filled" @click=${() => confirming.set(card.seq)}>
                      Restore to here
                    </button>
                    <button type="button" class="r2m-button r2m-button--stroked" @click=${() => viewing.set(null)}>
                      Back to latest
                    </button>
                  </div>`
            }
          </div>`
        : nothing
    }
    ${icon('history', 'proto-h__strip-icon')}
    <ol class="proto-h__ticks">
      ${list.map(
        (s) => html`<li>
          <button
            type="button"
            class="proto-h__tick ${s.run ? 'proto-h__tick--run' : ''} ${s.restoredFrom ? 'proto-h__tick--restore' : ''} ${
              v !== null && s.seq > v ? 'proto-h__tick--later' : ''
            } ${v === s.seq || (v === null && s === list.at(-1)) ? 'proto-h__tick--on' : ''}"
            data-tooltip="#${s.seq} ${s.label}${s.note ? ` (${s.note})` : ''} · ${s.touched} · ${s.time}"
            aria-label="#${s.seq} ${s.label}"
            @click=${() => {
              confirming.set(null);
              viewing.set(s === list.at(-1) ? null : s.seq);
            }}
          ></button>
        </li>`,
      )}
    </ol>
    <span class="proto-h__strip-now">${v === null ? 'Current' : `#${v} of ${list.length}`}</span>
  </div>`;
}

/* ---- toast after a Restore ----------------------------------------------------------------------- */

function toastView() {
  const t = toast();
  if (!t) return nothing;
  return html`<div class="proto-h__toast" role="status">
    ${icon('restart_alt')}<span>${t.text}</span>
    <a class="r2m-button r2m-button--stroked" href="#">Show in Book</a>
    ${
      t.undo !== undefined
        ? html`<button type="button" class="r2m-button r2m-button--stroked" @click=${() => restore(t.undo!, false)}>
            Undo restore
          </button>`
        : nothing
    }
    <button type="button" class="r2m-icon-button" aria-label="Dismiss" @click=${() => toast.set(null)}>
      ${icon('close')}
    </button>
  </div>`;
}

/* ---- floating switcher (copied from the Overview prototype) -------------------------------------- */

export function switcher(current: Variant, go: (v: Variant) => void) {
  const i = VARIANTS.indexOf(current);
  const prev = VARIANTS[(i + VARIANTS.length - 1) % VARIANTS.length]!;
  const next = VARIANTS[(i + 1) % VARIANTS.length]!;
  const reset = (v: Variant) => {
    viewing.set(null);
    historyOpen.set(false);
    confirming.set(null);
    toast.set(null);
    go(v);
  };
  return html`<div class="proto-switcher" role="toolbar" aria-label="Prototype variant">
    <button type="button" class="proto-switcher__arrow" aria-label="Previous variant" @click=${() => reset(prev)}>
      ${icon('arrow_back')}
    </button>
    <span class="proto-switcher__label"><b>${current}</b> ${VARIANT_NAMES[current]}</span>
    <button type="button" class="proto-switcher__arrow" aria-label="Next variant" @click=${() => reset(next)}>
      ${icon('arrow_forward')}
    </button>
  </div>`;
}

/** Arrow keys cycle variants unless a text field has focus. Returns the disposer. */
export function bindSwitcherKeys(current: () => Variant | null, go: (v: Variant) => void): () => void {
  const onKey = (e: KeyboardEvent) => {
    const now = current();
    if (!now) return;
    const t = e.target as HTMLElement | null;
    if (t && (t.matches('input, textarea, select') || t.isContentEditable)) return;
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const i = VARIANTS.indexOf(now);
    const d = e.key === 'ArrowRight' ? 1 : VARIANTS.length - 1;
    viewing.set(null);
    historyOpen.set(false);
    toast.set(null);
    go(VARIANTS[(i + d) % VARIANTS.length]!);
  };
  window.addEventListener('keydown', onKey);
  return () => window.removeEventListener('keydown', onKey);
}
