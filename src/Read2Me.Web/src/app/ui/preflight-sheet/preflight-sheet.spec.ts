import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  PreflightPhase,
  PreflightPlan,
  PreflightServiceProgress,
  PreflightSheet,
} from './preflight-sheet';

@Component({
  imports: [PreflightSheet],
  template: `<r2m-preflight-sheet
    taskLabel="Attribution"
    [phase]="phase()"
    [plan]="plan"
    [progress]="progress()"
    [error]="error"
    (start)="log.push('start')"
    (cancel)="log.push('cancel')"
    (close)="log.push('close')"
  />`,
})
class HostCmp {
  readonly phase = signal<PreflightPhase>('plan');
  plan: PreflightPlan = {
    ready: false,
    toStart: [{ name: 'llama', status: 'Stopped' }],
    conflicts: [{ name: 'chatterbox', reason: 'GPU: one model at a time' }],
  };
  readonly progress = signal<PreflightServiceProgress[]>([]);
  error: string | null = null;
  log: string[] = [];
}

describe('r2m-preflight-sheet', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('plan phase lists conflicts and services to start with Cancel / Start', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h2')?.textContent).toBe('Attribution needs AI services');
    const names = Array.from(el.querySelectorAll('.r2m-preflight-sheet__name')).map(
      (n) => n.textContent,
    );
    expect(names).toEqual(['chatterbox', 'llama']);
    expect(el.querySelector('.r2m-preflight-sheet__reason')?.textContent).toBe(
      'GPU: one model at a time',
    );
    expect(el.querySelector('r2m-status-chip')?.textContent).toContain('Stopped');

    const buttons = Array.from(el.querySelectorAll('footer button')) as HTMLButtonElement[];
    expect(buttons.map((b) => b.textContent?.trim())).toEqual(['Cancel', 'Start services']);
    buttons[1]!.click();
    buttons[0]!.click();
    expect(fixture.componentInstance.log).toEqual(['start', 'cancel']);
  });

  it('running phase shows a spinner for active stages and chips per stage', async () => {
    const fixture = await mount();
    fixture.componentInstance.phase.set('running');
    fixture.componentInstance.progress.set([
      { name: 'chatterbox', stage: 'stopped' },
      { name: 'llama', stage: 'starting' },
    ]);
    await fixture.whenStable();
    const rows = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.r2m-preflight-sheet__row'),
    );
    expect(rows[0]?.querySelector('mat-icon')?.textContent).toBe('stop_circle');
    expect(rows[1]?.querySelector('mat-progress-spinner')).not.toBeNull();
    expect(rows[1]?.querySelector('r2m-status-chip')?.textContent).toContain('Starting');
  });

  it('done and failed phases render their summaries; Close emits', async () => {
    const fixture = await mount();
    fixture.componentInstance.phase.set('done');
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'All required services are ready',
    );

    fixture.componentInstance.phase.set('failed');
    fixture.componentInstance.progress.set([{ name: 'llama', stage: 'failed', error: 'OOM' }]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.r2m-preflight-sheet__error')?.textContent).toContain(
      'failed to start',
    );
    expect(el.querySelector('.r2m-preflight-sheet__reason')?.textContent).toBe('OOM');
    (el.querySelector('footer button') as HTMLButtonElement).click();
    expect(fixture.componentInstance.log).toEqual(['close']);
  });
});
