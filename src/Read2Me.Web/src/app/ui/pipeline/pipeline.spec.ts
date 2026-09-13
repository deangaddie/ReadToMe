import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Pipeline, PipelineActionEvent, PipelineStepView } from './pipeline';

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

@Component({
  imports: [Pipeline],
  template: `<r2m-pipeline [steps]="steps" [busyAction]="busy()" (act)="events.push($event)" />`,
})
class HostCmp {
  readonly steps = STEPS;
  readonly busy = signal<string | null>(null);
  readonly events: PipelineActionEvent[] = [];
}

describe('r2m-pipeline', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el };
  }

  it('renders each step with its chip, marks done and next, and numbers the rest', async () => {
    const { el } = await mount();
    const steps = Array.from(el.querySelectorAll('.r2m-pipeline__step'));

    expect(steps.map((s) => s.getAttribute('data-step'))).toEqual(['import', 'cast']);
    expect(steps[0]!.classList.contains('r2m-pipeline__step--done')).toBe(true);
    expect(steps[0]!.querySelector('.r2m-pipeline__marker')?.textContent?.trim()).toBe('check');
    expect(steps[1]!.getAttribute('aria-current')).toBe('step');
    expect(steps[1]!.querySelector('r2m-status-chip')?.textContent).toContain('Not started');
  });

  it('emits step + action, and honours disabled', async () => {
    const { fixture, el } = await mount();
    const discover = el.querySelector('[data-action="discover"]') as HTMLButtonElement;
    const openCast = el.querySelector('[data-action="openCast"]') as HTMLButtonElement;

    discover.click();
    expect(fixture.componentInstance.events).toEqual([{ step: 'cast', action: 'discover' }]);
    expect(openCast.disabled).toBe(true);
    expect(
      el
        .querySelector('[data-action="reread"]')!
        .classList.contains('r2m-pipeline__action--destructive'),
    ).toBe(true);
  });

  it('disables every action while one is busy and spins that one', async () => {
    const { fixture, el } = await mount();
    fixture.componentInstance.busy.set('cast:discover');
    await fixture.whenStable();

    const buttons = Array.from(el.querySelectorAll<HTMLButtonElement>('.r2m-pipeline__action'));
    expect(buttons.every((b) => b.disabled)).toBe(true);
    expect(el.querySelector('[data-action="discover"] mat-progress-spinner')).not.toBeNull();
  });
});
