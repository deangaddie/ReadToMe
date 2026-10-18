import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { html } from 'lit-html';
import {
  type PipelineActionEvent,
  type PipelineCheckpointEvent,
  type PipelineStepView,
  pipeline,
} from './pipeline';

const STEPS: PipelineStepView[] = [
  {
    id: 'import',
    title: 'Import',
    icon: 'upload_file',
    chip: { status: 'ok', label: 'Done' },
    detail: 'Book read in',
    next: false,
    checkpoint: [],
    laterDone: 0,
    actions: [
      {
        id: 'reread',
        label: 'Reread…',
        icon: 'restart_alt',
        primary: false,
        destructive: true,
        blockedReason: null,
      },
    ],
  },
  {
    id: 'speakers',
    title: 'Speakers',
    icon: 'record_voice_over',
    chip: { status: 'neutral', label: 'Review not confirmed' },
    detail: 'Assign a speaker to every line.',
    next: true,
    checkpoint: [{ id: 'confirm', label: 'Confirm review' }],
    laterDone: 2,
    callout: { kind: 'warn', text: '7 items flagged for review.' },
    advice: 'Usually done after Structure: a reread discards attribution.',
    actions: [
      {
        id: 'attribute',
        label: 'Attribute unprocessed',
        icon: 'auto_awesome',
        primary: true,
        blockedReason: 'Every paragraph has a speaker',
      },
      { id: 'openCast', label: 'Open cast', icon: 'groups', primary: false, blockedReason: null },
    ],
  },
];

afterEach(() => document.body.replaceChildren());

function mount(busyAction: string | null = null, helpOpen: string | null = null) {
  const host = document.createElement('div');
  document.body.append(host);
  const events: PipelineActionEvent[] = [];
  const checkpoints: PipelineCheckpointEvent[] = [];
  const helps: string[] = [];
  render(
    pipeline({
      steps: STEPS,
      busyAction,
      helpOpen,
      onAct: (e) => events.push(e),
      onCheckpoint: (e) => checkpoints.push(e),
      onHelp: (step) => helps.push(step),
      renderHelp: (step) => html`<em class="fake-help">${step.title} help</em>`,
    }),
    host,
  );
  return { el: host, events, checkpoints, helps };
}

describe('pipeline()', () => {
  it('renders each step with its chip, marks done and next, and numbers the rest', () => {
    const { el } = mount();
    const steps = Array.from(el.querySelectorAll('.r2m-pipeline__step'));

    expect(steps.map((s) => s.getAttribute('data-step'))).toEqual(['import', 'speakers']);
    expect(steps[0]!.classList.contains('r2m-pipeline__step--done')).toBe(true);
    expect(steps[0]!.querySelector('.r2m-pipeline__marker')?.textContent?.trim()).toBe('check');
    expect(steps[1]!.getAttribute('aria-current')).toBe('step');
    expect(steps[0]!.hasAttribute('aria-current')).toBe(false);
    expect(steps[1]!.querySelector('.r2m-status-chip')?.textContent).toContain(
      'Review not confirmed',
    );
  });

  it('emits step + action; a blocked action is disabled and its wrapper carries the reason', () => {
    const { el, events } = mount();
    const openCast = el.querySelector<HTMLButtonElement>('[data-action="openCast"]')!;
    const attribute = el.querySelector<HTMLButtonElement>('[data-action="attribute"]')!;

    openCast.click();
    expect(events).toEqual([{ step: 'speakers', action: 'openCast' }]);
    expect(openCast.disabled).toBe(false);
    expect(openCast.closest('.r2m-blockable')?.hasAttribute('data-tooltip')).toBe(false);

    expect(attribute.disabled).toBe(true);
    const wrapper = attribute.closest('.r2m-blockable')!;
    expect(wrapper.getAttribute('data-tooltip')).toBe('Every paragraph has a speaker');
    expect(wrapper.getAttribute('tabindex')).toBe('0');
    expect(
      el
        .querySelector('[data-action="reread"]')!
        .classList.contains('r2m-pipeline__action--destructive'),
    ).toBe(true);
  });

  it('disables every action while one is busy and spins that one, with no reason', () => {
    const { el } = mount('speakers:openCast');

    const buttons = Array.from(el.querySelectorAll<HTMLButtonElement>('.r2m-pipeline__action'));
    expect(buttons.every((b) => b.disabled)).toBe(true);
    expect(el.querySelector('[data-action="openCast"] .r2m-spinner')).not.toBeNull();
    expect(el.querySelector('[data-action="reread"] .r2m-spinner')).toBeNull();
    expect(
      el
        .querySelector('[data-action="openCast"]')!
        .closest('.r2m-blockable')!
        .hasAttribute('data-tooltip'),
    ).toBe(false);
  });

  it('row anatomy (variant B): pills and the hint beside the chip, callout under the detail, advice under the actions', () => {
    const { el, checkpoints } = mount();
    const speakers = el.querySelector('[data-step="speakers"]')!;
    const head = speakers.querySelector('.r2m-pipeline__head')!;
    const pill = head.querySelector<HTMLButtonElement>('[data-checkpoint="confirm"]')!;
    expect(pill.textContent).toContain('Confirm review');
    expect(pill.closest('.r2m-pipeline__checkpoint')).not.toBeNull();
    expect(head.querySelector('.r2m-pipeline__hint')?.textContent).toBe(
      '2 later steps already done',
    );
    pill.click();
    expect(checkpoints).toEqual([{ step: 'speakers', pill: 'confirm' }]);

    const body = speakers.querySelector('.r2m-pipeline__body')!;
    const order = [...body.children].map((c) => c.className.split(' ')[0]);
    expect(order).toEqual([
      'r2m-pipeline__head',
      'r2m-pipeline__detail',
      'r2m-callout',
      'r2m-pipeline__actions',
      'r2m-advice',
    ]);
    expect(body.querySelector('.r2m-callout')?.textContent).toContain(
      '7 items flagged for review.',
    );
    expect(body.querySelector('.r2m-advice')?.textContent).toBe(
      'Usually done after Structure: a reread discards attribution.',
    );

    const importStep = el.querySelector('[data-step="import"]')!;
    expect(importStep.querySelector('.r2m-pipeline__checkpoint')).toBeNull();
    expect(importStep.querySelector('.r2m-pipeline__hint')).toBeNull();
    expect(importStep.querySelector('.r2m-callout')).toBeNull();
    expect(importStep.querySelector('.r2m-advice')).toBeNull();
  });

  it('the ? toggles help: the open row is marked, its inline help follows the head row, the others stay closed', () => {
    const { el, helps } = mount(null, 'speakers');
    const speakers = el.querySelector('[data-step="speakers"]')!;
    const toggle = speakers.querySelector<HTMLButtonElement>('.r2m-pipeline__help-toggle')!;
    expect(toggle.getAttribute('aria-label')).toBe('Help for Speakers');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(speakers.classList.contains('r2m-pipeline__step--help-open')).toBe(true);
    const inline = speakers.querySelector('.r2m-pipeline__help-inline')!;
    expect(inline.previousElementSibling?.classList.contains('r2m-pipeline__head')).toBe(true);
    expect(inline.nextElementSibling?.classList.contains('r2m-pipeline__detail')).toBe(true);
    expect(inline.querySelector('.fake-help')?.textContent).toBe('Speakers help');

    const importStep = el.querySelector('[data-step="import"]')!;
    expect(
      importStep.querySelector('.r2m-pipeline__help-toggle')?.getAttribute('aria-expanded'),
    ).toBe('false');
    expect(importStep.querySelector('.r2m-pipeline__help-inline')).toBeNull();
    expect(importStep.classList.contains('r2m-pipeline__step--help-open')).toBe(false);

    toggle.click();
    importStep.querySelector<HTMLButtonElement>('.r2m-pipeline__help-toggle')!.click();
    expect(helps).toEqual(['speakers', 'import']);
  });

  it('a busy command disables the pills too and spins a busy pill', () => {
    const { el } = mount('speakers:confirm');
    const pill = el.querySelector<HTMLButtonElement>('[data-checkpoint="confirm"]')!;
    expect(pill.disabled).toBe(true);
    expect(pill.querySelector('.r2m-spinner')).not.toBeNull();
  });
});
