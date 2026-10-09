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
        disabled: false,
      },
    ],
  },
  {
    id: 'cast',
    title: 'Cast',
    icon: 'groups',
    chip: { status: 'neutral', label: 'Not started' },
    detail: 'Find who speaks',
    next: true,
    actions: [
      {
        id: 'discover',
        label: 'Discover characters',
        icon: 'person_search',
        primary: true,
        disabled: false,
      },
      { id: 'openCast', label: 'Open cast', icon: 'groups', primary: false, disabled: true },
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

    expect(steps.map((s) => s.getAttribute('data-step'))).toEqual(['import', 'cast']);
    expect(steps[0]!.classList.contains('r2m-pipeline__step--done')).toBe(true);
    expect(steps[0]!.querySelector('.r2m-pipeline__marker')?.textContent?.trim()).toBe('check');
    expect(steps[1]!.getAttribute('aria-current')).toBe('step');
    expect(steps[0]!.hasAttribute('aria-current')).toBe(false);
    expect(steps[1]!.querySelector('.r2m-status-chip')?.textContent).toContain('Not started');
  });

  it('emits step + action, and honours disabled', () => {
    const { el, events } = mount();
    const discover = el.querySelector<HTMLButtonElement>('[data-action="discover"]')!;
    const openCast = el.querySelector<HTMLButtonElement>('[data-action="openCast"]')!;

    discover.click();
    expect(events).toEqual([{ step: 'cast', action: 'discover' }]);
    expect(openCast.disabled).toBe(true);
    expect(
      el
        .querySelector('[data-action="reread"]')!
        .classList.contains('r2m-pipeline__action--destructive'),
    ).toBe(true);
  });

  it('disables every action while one is busy and spins that one', () => {
    const { el } = mount('cast:discover');

    const buttons = Array.from(el.querySelectorAll<HTMLButtonElement>('.r2m-pipeline__action'));
    expect(buttons.every((b) => b.disabled)).toBe(true);
    expect(el.querySelector('[data-action="discover"] .r2m-spinner')).not.toBeNull();
    expect(el.querySelector('[data-action="reread"] .r2m-spinner')).toBeNull();
  });
});
