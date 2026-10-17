/**
 * PROTOTYPE — throwaway. Three variants of the nine-step Overview, switchable via `?variant=A|B|C`
 * on the existing `/projects/{folder}` route. Answers
 * `.scratch/guidance-primitives/issues/03-overview-prototype.md`: does one stepper still fit at
 * laptop and phone width, which help-panel shape reads better, where do checkpoint controls sit.
 * Fake step data; every control is a stub. Delete with the branch.
 */
import { html, nothing } from 'lit-html';
import { unsafeHTML } from 'lit-html/directives/unsafe-html.js';
import { signal } from '@app/core/signals';
import type { IconName } from '@app/ui/icons';
import { type StatusKind, icon, statusChip } from '@app/ui/partials';

export const VARIANTS = ['A', 'B', 'C'] as const;
export type Variant = (typeof VARIANTS)[number];
export const VARIANT_NAMES: Record<Variant, string> = {
  A: 'One stepper · inline help · controls in action group',
  B: 'One stepper · help side panel · controls beside chip',
  C: 'Two levels · compact list, expanded step · inline help',
};

type Checkpoint = 'confirmed' | 'skipped' | 'stale' | null;
type CheckpointControl = 'confirm' | 'skip' | 'reopen' | 'reconfirm' | 'reconfirmSkip';

interface ProtoAction {
  label: string;
  icon: IconName;
  primary?: boolean;
  destructive?: boolean;
}

interface ProtoStep {
  id: string;
  title: string;
  icon: IconName;
  conditional: boolean;
  checkpoint: Checkpoint;
  done: boolean;
  next: boolean;
  chip: { status: StatusKind; label: string };
  detail: string;
  actions: ProtoAction[];
  controls: CheckpointControl[];
  /** State-driven banner on the row (rule 5). */
  callout?: string;
  /** One sentence under the action group (rule 5, no-dialog actions). */
  advice?: string;
  /** "N later steps already done" when the next marker is held back. */
  hint?: string;
  help: string;
}

const help = (what: string, how: string[], done: string) =>
  `<p>${what}</p><h4>How</h4><ul>${how.map((h) => `<li>${h}</li>`).join('')}</ul><h4>Done when</h4><p>${done}</p>`;

/** One realistic mid-production project: Import done, Structure stale, Text skipped, the rest open. */
export const STEPS: ProtoStep[] = [
  {
    id: 'import',
    title: 'Import',
    icon: 'upload_file',
    conditional: false,
    checkpoint: null,
    done: true,
    next: false,
    chip: { status: 'ok', label: 'Done' },
    detail: 'Book read in · 2,418 items',
    actions: [{ label: 'Reread… (auto splits)', icon: 'restart_alt', destructive: true }],
    controls: [],
    help: help(
      'Read the book file into volumes, parts, chapters and paragraphs.',
      ['Drop an EPUB or text file on the project card.', 'Use <b>Read book</b>.'],
      'The project has content.',
    ),
  },
  {
    id: 'structure',
    title: 'Structure',
    icon: 'library_books',
    conditional: true,
    checkpoint: 'stale',
    done: false,
    next: true,
    chip: { status: 'warn', label: 'Changed since you confirmed' },
    detail: 'Confirmed 14 Oct · 2 chapters split since. 31 chapters in 3 parts.',
    actions: [
      { label: 'Check structure', icon: 'menu_book', primary: true },
      { label: 'Reread with split rules…', icon: 'call_split' },
    ],
    controls: ['reconfirm'],
    callout: 'Structure looks off: chapter 12 holds 1,900 paragraphs, five times the median.',
    hint: 'Later steps already have progress',
    help: help(
      'Compare the tree with the printed book before anything depends on it.',
      [
        'Open the Book page in Read mode with the tree open.',
        'If chapters, parts or volumes are missing or merged, use <b>Reread with split rules…</b>.',
        'Fix headings with the Node menu.',
      ],
      'You have confirmed the structure, or skipped the check.',
    ),
  },
  {
    id: 'speakers',
    title: 'Speakers',
    icon: 'record_voice_over',
    conditional: false,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'warn', label: '12 paragraphs unassigned' },
    detail: '12 paragraphs with unassigned speakers · review not confirmed',
    actions: [
      { label: 'Attribute unprocessed', icon: 'auto_awesome', primary: true },
      { label: 'Open in Speakers mode', icon: 'menu_book' },
      { label: 'Open cast', icon: 'groups' },
    ],
    controls: ['confirm'],
    callout: '7 items flagged for review in Audio mode.',
    help: help(
      'Give every line a speaker. Attribution discovers most of the cast on its own.',
      [
        'Run <b>Attribute unprocessed</b>.',
        'Merge duplicate characters the AI created for one person with two names.',
        'Fix mis-assignments in Speakers mode; bulk assign where a scene is wrong.',
      ],
      'Zero paragraphs are unassigned and you have confirmed the review.',
    ),
  },
  {
    id: 'text',
    title: 'Text',
    icon: 'edit',
    conditional: true,
    checkpoint: 'skipped',
    done: true,
    next: false,
    chip: { status: 'neutral', label: 'Skipped' },
    detail: 'Skipped 15 Oct.',
    actions: [
      { label: 'Review text', icon: 'menu_book', primary: true },
      { label: 'Edit with AI…', icon: 'auto_fix_high' },
    ],
    controls: ['reopen'],
    help: help(
      'Edit paragraphs and headings before they are narrated.',
      ['Read through in Read mode.', 'Use <b>Edit with AI…</b> for repetitive fixes.'],
      'You have confirmed the text, or skipped the review.',
    ),
  },
  {
    id: 'voices',
    title: 'Voices',
    icon: 'graphic_eq',
    conditional: false,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'warn', label: '4 of 6 speakers voiced' },
    detail: '4 of 6 speakers have a voice with audio',
    actions: [
      { label: 'Open cast', icon: 'groups', primary: true },
      { label: 'Voice rules', icon: 'tune' },
    ],
    controls: [],
    help: help(
      'Design or pick a voice for every speaker.',
      ['Open the cast; each card has a prompt and a Generate audio button.', 'Voice rules change a voice by position.'],
      'Every speaker with lines has a voice with audio.',
    ),
  },
  {
    id: 'titles',
    title: 'Titles',
    icon: 'title',
    conditional: true,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'neutral', label: 'Not started' },
    detail: 'Insert book, part and chapter title narration.',
    actions: [{ label: 'Add titles…', icon: 'add', primary: true }],
    controls: ['confirm', 'skip'],
    advice: 'Usually done after Structure: a reread drops inserted titles.',
    help: help(
      'Narrate the headings so the listener hears where they are.',
      ['Use <b>Add titles…</b> and tick the levels that add navigation.'],
      'You have confirmed the titles, or skipped them.',
    ),
  },
  {
    id: 'pauses',
    title: 'Pauses',
    icon: 'pause',
    conditional: true,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'neutral', label: 'Not started' },
    detail: 'Insert pauses after the structure is final.',
    actions: [{ label: 'Add pauses', icon: 'add', primary: true }],
    controls: ['skip'],
    advice: 'Usually done after Structure: a reread drops inserted pauses.',
    help: help(
      'A pause after each chapter gives the listener a breath.',
      ['Use <b>Add pauses</b>.'],
      'At least one pause paragraph exists, or you skipped the step.',
    ),
  },
  {
    id: 'audio',
    title: 'Audio',
    icon: 'volume_up',
    conditional: false,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'warn', label: '312 missing audio' },
    detail: '312 paragraphs missing audio · 2,106 of 2,418 items generated',
    actions: [
      { label: 'Generate needed audio', icon: 'play_circle', primary: true },
      { label: 'Open in Audio mode', icon: 'menu_book' },
    ],
    controls: [],
    advice: 'Later steps add or change narration that will need audio.',
    help: help(
      'Synthesise every line, listen, regenerate what sounds wrong.',
      ['Use <b>Generate needed audio</b>; watch progress in Activity.', 'Audio mode shows items flagged for review.'],
      'Zero items are missing audio.',
    ),
  },
  {
    id: 'export',
    title: 'Export',
    icon: 'download',
    conditional: false,
    checkpoint: null,
    done: false,
    next: false,
    chip: { status: 'neutral', label: 'Not started' },
    detail: 'Assemble the .m4b audiobook with chapters and cover art.',
    actions: [{ label: 'Assemble', icon: 'library_music', primary: true }],
    controls: [],
    help: help(
      'Assemble the finished audiobook.',
      ['Use <b>Assemble</b>. With audio missing you are warned and offered a partial build.'],
      'A full build exists.',
    ),
  },
];

const CONTROL_LABEL: Record<CheckpointControl, { label: string; icon: IconName }> = {
  confirm: { label: 'Confirm', icon: 'check' },
  skip: { label: 'Skip', icon: 'skip_next' },
  reopen: { label: 'Reopen', icon: 'history' },
  reconfirm: { label: 'Reconfirm', icon: 'check' },
  reconfirmSkip: { label: 'Reconfirm skip', icon: 'skip_next' },
};

/* ---- prototype-local state (in memory; resets on reload) ----------------------------------- */

/** Step whose help is open (A: inline; B: side panel; C: inline within the expanded step). */
export const helpOpen = signal<string | null>(null);
/** C only: the expanded step; starts on the next step. */
export const expanded = signal<string>(STEPS.find((s) => s.next)?.id ?? 'import');
const stub = (what: string) => () => console.info(`[prototype] ${what}`);

/* ---- shared bits (small; each variant owns its layout) ------------------------------------ */

const actionButton = (a: ProtoAction) =>
  html`<button
    type="button"
    class="r2m-button ${a.primary ? 'r2m-button--filled r2m-pipeline__action--primary' : ''} r2m-pipeline__action ${a.destructive ? 'r2m-pipeline__action--destructive' : ''}"
    @click=${stub(a.label)}
  >
    ${icon(a.icon)} ${a.label}
  </button>`;

const controlButton = (c: CheckpointControl, compact = false) =>
  html`<button
    type="button"
    class="r2m-button proto-control ${compact ? 'proto-control--compact' : ''} ${c === 'reconfirm' || c === 'reconfirmSkip' ? 'proto-control--stale' : ''}"
    @click=${stub(c)}
  >
    ${icon(CONTROL_LABEL[c].icon)} ${CONTROL_LABEL[c].label}
  </button>`;

const callout = (text: string) =>
  html`<div class="proto-callout" role="status">${icon('warning')}<span>${text}</span></div>`;

const adviceLine = (text: string) => html`<p class="proto-advice">${icon('info')} ${text}</p>`;

const helpToggle = (step: ProtoStep) =>
  html`<button
    type="button"
    class="r2m-icon-button proto-help-toggle ${helpOpen() === step.id ? 'proto-help-toggle--on' : ''}"
    aria-label="Help for ${step.title}"
    aria-expanded=${helpOpen() === step.id}
    data-tooltip="How this step works"
    @click=${() => helpOpen.set(helpOpen() === step.id ? null : step.id)}
  >
    ${icon('question_mark')}
  </button>`;

const helpInline = (step: ProtoStep) =>
  helpOpen() === step.id
    ? html`<div class="proto-help proto-help--inline">${unsafeHTML(step.help)}</div>`
    : nothing;

const marker = (step: ProtoStep, i: number) =>
  html`<span class="r2m-pipeline__marker"
    >${step.done ? icon('check') : step.next ? icon(step.icon) : i + 1}</span
  >`;

const rowClass = (step: ProtoStep) =>
  `r2m-pipeline__step ${step.next ? 'r2m-pipeline__step--next' : ''} ${step.done ? 'r2m-pipeline__step--done' : ''} ${step.checkpoint === 'stale' ? 'proto-step--stale' : ''}`;

/* ---- Variant A: one stepper, every row open; help inline; controls in the action group ----- */

export function variantA() {
  return html`<ol class="r2m-pipeline proto-pipeline proto-pipeline--a">
    ${STEPS.map(
      (step, i) => html`<li class=${rowClass(step)} data-step=${step.id}>
        <div class="r2m-pipeline__rail" aria-hidden="true">
          ${marker(step, i)}
          ${i === STEPS.length - 1 ? nothing : html`<span class="r2m-pipeline__line"></span>`}
        </div>
        <div class="r2m-pipeline__body">
          <div class="r2m-pipeline__head">
            <h3 class="r2m-pipeline__title">${step.title}</h3>
            <span class="r2m-pipeline__chip">${statusChip({ ...step.chip, compact: true })}</span>
            ${step.hint ? html`<span class="proto-hint">${step.hint}</span>` : nothing}
            <span class="proto-head-spacer"></span>
            ${helpToggle(step)}
          </div>
          <p class="r2m-pipeline__detail">${step.detail}</p>
          ${step.callout ? callout(step.callout) : nothing}
          <div class="r2m-pipeline__actions">
            ${step.actions.map(actionButton)}
            ${step.controls.length ? html`<span class="proto-actions-gap"></span>` : nothing}
            ${step.controls.map((c) => controlButton(c))}
          </div>
          ${step.advice ? adviceLine(step.advice) : nothing} ${helpInline(step)}
        </div>
      </li>`,
    )}
  </ol>`;
}

/* ---- Variant B: one stepper; help in the aside; controls beside the chip ------------------- */

export function variantB() {
  return html`<ol class="r2m-pipeline proto-pipeline proto-pipeline--b">
    ${STEPS.map(
      (step, i) => html`<li
        class="${rowClass(step)} ${helpOpen() === step.id ? 'proto-step--help-open' : ''}"
        data-step=${step.id}
      >
        <div class="r2m-pipeline__rail" aria-hidden="true">
          ${marker(step, i)}
          ${i === STEPS.length - 1 ? nothing : html`<span class="r2m-pipeline__line"></span>`}
        </div>
        <div class="r2m-pipeline__body">
          <div class="r2m-pipeline__head">
            <h3 class="r2m-pipeline__title">${step.title}</h3>
            <span class="r2m-pipeline__chip">${statusChip({ ...step.chip, compact: true })}</span>
            ${step.controls.map((c) => controlButton(c, true))}
            ${step.hint ? html`<span class="proto-hint">${step.hint}</span>` : nothing}
            <span class="proto-head-spacer"></span>
            ${helpToggle(step)}
          </div>
          <p class="r2m-pipeline__detail">${step.detail}</p>
          ${step.callout ? callout(step.callout) : nothing}
          <div class="r2m-pipeline__actions">${step.actions.map(actionButton)}</div>
          ${step.advice ? adviceLine(step.advice) : nothing}
        </div>
      </li>`,
    )}
  </ol>`;
}

/** B's aside: the step's help replaces the project details card while open. */
export function variantBAside() {
  const step = STEPS.find((s) => s.id === helpOpen());
  if (!step) return null;
  return html`<section class="proto-help proto-help--panel" aria-label="Help: ${step.title}">
    <header class="proto-help__head">
      <h2 class="proto-help__title">${icon(step.icon)} ${step.title}</h2>
      <button
        type="button"
        class="r2m-icon-button"
        aria-label="Close help"
        @click=${() => helpOpen.set(null)}
      >
        ${icon('close')}
      </button>
    </header>
    ${unsafeHTML(step.help)}
    <p class="proto-help__foot">
      <a href="#" @click=${(e: Event) => e.preventDefault()}>${icon('open_in_new')} Open the guide</a>
    </p>
  </section>`;
}

/* ---- Variant C: two levels; compact rows, one expanded; help inline in the expanded step --- */

export function variantC() {
  return html`<ol class="r2m-pipeline proto-pipeline proto-pipeline--c">
    ${STEPS.map((step, i) => {
      const open = expanded() === step.id;
      return html`<li
        class="${rowClass(step)} ${open ? 'proto-step--open' : 'proto-step--closed'}"
        data-step=${step.id}
      >
        <div class="r2m-pipeline__rail" aria-hidden="true">
          ${marker(step, i)}
          ${i === STEPS.length - 1 ? nothing : html`<span class="r2m-pipeline__line"></span>`}
        </div>
        <div class="r2m-pipeline__body">
          <button
            type="button"
            class="proto-row-toggle"
            aria-expanded=${open}
            @click=${() => {
              expanded.set(open ? '' : step.id);
              helpOpen.set(null);
            }}
          >
            <h3 class="r2m-pipeline__title">${step.title}</h3>
            <span class="r2m-pipeline__chip">${statusChip({ ...step.chip, compact: true })}</span>
            ${
              !open && step.callout
                ? html`<span class="proto-flag" data-tooltip=${step.callout}>${icon('warning')}</span>`
                : nothing
            }
            <span class="proto-head-spacer"></span>
            ${icon(open ? 'expand_less' : 'expand_more', 'proto-row-chevron')}
          </button>
          ${
            open
              ? html`
                  <p class="r2m-pipeline__detail">
                    ${step.detail}
                    ${step.hint ? html`<span class="proto-hint">· ${step.hint}</span>` : nothing}
                  </p>
                  ${step.callout ? callout(step.callout) : nothing}
                  <div class="r2m-pipeline__actions">
                    ${step.actions.map(actionButton)}
                    ${step.controls.length ? html`<span class="proto-actions-gap"></span>` : nothing}
                    ${step.controls.map((c) => controlButton(c))}
                    <span class="proto-head-spacer"></span>
                    ${helpToggle(step)}
                  </div>
                  ${step.advice ? adviceLine(step.advice) : nothing} ${helpInline(step)}
                `
              : nothing
          }
        </div>
      </li>`;
    })}
  </ol>`;
}

/* ---- floating switcher ------------------------------------------------------------------- */

export function switcher(current: Variant, go: (v: Variant) => void) {
  const i = VARIANTS.indexOf(current);
  const prev = VARIANTS[(i + VARIANTS.length - 1) % VARIANTS.length]!;
  const next = VARIANTS[(i + 1) % VARIANTS.length]!;
  return html`<div class="proto-switcher" role="toolbar" aria-label="Prototype variant">
    <button type="button" class="proto-switcher__arrow" aria-label="Previous variant" @click=${() => go(prev)}>
      ${icon('arrow_back')}
    </button>
    <span class="proto-switcher__label"><b>${current}</b> ${VARIANT_NAMES[current]}</span>
    <button type="button" class="proto-switcher__arrow" aria-label="Next variant" @click=${() => go(next)}>
      ${icon('arrow_forward')}
    </button>
  </div>`;
}

/** Arrow keys cycle variants unless a text field has focus. Returns the disposer. */
export function bindSwitcherKeys(current: () => Variant, go: (v: Variant) => void): () => void {
  const onKey = (e: KeyboardEvent) => {
    const t = e.target as HTMLElement | null;
    if (t && (t.matches('input, textarea, select') || t.isContentEditable)) return;
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const i = VARIANTS.indexOf(current());
    const d = e.key === 'ArrowRight' ? 1 : VARIANTS.length - 1;
    go(VARIANTS[(i + d) % VARIANTS.length]!);
  };
  window.addEventListener('keydown', onKey);
  return () => window.removeEventListener('keydown', onKey);
}
