import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { type PipelineActionEvent, type PipelineStepView, pipeline } from './pipeline';

const STEPS: PipelineStepView[] = [
  {
    id: 'import',
    title: 'Import',
    icon: 'upload_file',
    chip: { status: 'ok', label: 'Done' },
    detail: 'Book read in',
    next: false,
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

function mount(busyAction: string | null = null) {
  const host = document.createElement('div');
  document.body.append(host);
  const events: PipelineActionEvent[] = [];
  render(pipeline({ steps: STEPS, busyAction, onAct: (e) => events.push(e) }), host);
  return { el: host, events };
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
});
