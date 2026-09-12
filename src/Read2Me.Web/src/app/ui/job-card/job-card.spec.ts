import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { JobView } from '@app/ui/job-pill/job';
import { JobCard } from './job-card';

@Component({
  imports: [JobCard],
  template: `<r2m-job-card
    [job]="job()"
    [history]="history"
    (cancel)="cancelled = $event"
    (dismiss)="dismissed = $event"
  />`,
})
class HostCmp {
  readonly job = signal<JobView>({
    id: 'audio',
    kind: 'audio',
    label: 'Generating audio',
    state: 'running',
    completed: 25,
    total: 100,
    rate: '2.0 s/item',
    etaSeconds: 150,
    cancellable: true,
    dismissible: true,
  });
  history = [1, 3, 2, 5];
  cancelled = '';
  dismissed = '';
}

describe('r2m-job-card', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders numbers, determinate progress, history and actions', async () => {
    const fixture = await mount();
    const el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-card')!;
    const labels = Array.from(el.querySelectorAll('dt')).map((d) => d.textContent);
    expect(labels).toEqual(['Done', 'Total', 'Rate', 'ETA']);
    expect(el.querySelector('dd')?.textContent).toBe('25');
    expect(el.querySelector('mat-progress-bar')?.getAttribute('mode')).toBe('determinate');
    expect(el.querySelector('svg polyline')?.getAttribute('points')?.split(' ').length).toBe(4);
    expect(el.querySelector('r2m-status-chip')?.textContent).toContain('Running');

    (el.querySelectorAll('footer button')[0] as HTMLButtonElement).click();
    (el.querySelectorAll('footer button')[1] as HTMLButtonElement).click();
    expect(fixture.componentInstance.cancelled).toBe('audio');
    expect(fixture.componentInstance.dismissed).toBe('audio');
  });

  it('shows indeterminate progress without totals and an error when failed', async () => {
    const fixture = await mount();
    fixture.componentInstance.job.set({
      id: 'a',
      kind: 'assembly',
      label: 'Assembly',
      state: 'queued',
    });
    await fixture.whenStable();
    let el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-card')!;
    expect(el.querySelector('mat-progress-bar')?.getAttribute('mode')).toBe('indeterminate');

    fixture.componentInstance.job.set({
      id: 'a',
      kind: 'assembly',
      label: 'Assembly',
      state: 'failed',
      error: 'ffmpeg exited 1',
    });
    await fixture.whenStable();
    el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-card')!;
    expect(el.querySelector('mat-progress-bar')).toBeNull();
    expect(el.querySelector('.r2m-job-card__error')?.textContent).toContain('ffmpeg exited 1');
    expect(el.querySelector('footer')?.children.length).toBe(0);
  });
});
