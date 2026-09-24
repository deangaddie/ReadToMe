import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { formatDuration, jobSummary, JobView } from './job';
import { JobPill } from './job-pill';

@Component({
  imports: [JobPill],
  template: `<r2m-job-pill [job]="job()" (open)="opened = $event" (cancel)="cancelled = $event" />`,
})
class HostCmp {
  readonly job = signal<JobView>({
    id: 'attr',
    kind: 'attribution',
    label: 'Attributing',
    state: 'running',
    queued: 12,
    rate: '3.1 s/para',
    etaSeconds: 38,
    cancellable: true,
  });
  opened = '';
  cancelled = '';
}

describe('formatDuration', () => {
  it('formats seconds, minutes and hours', () => {
    expect(formatDuration(38)).toBe('0:38');
    expect(formatDuration(724)).toBe('12:04');
    expect(formatDuration(3729)).toBe('1:02:09');
    expect(formatDuration(null)).toBe('0:00');
    expect(formatDuration(-5)).toBe('0:00');
  });
});

describe('jobSummary', () => {
  it('joins the key numbers', () => {
    expect(
      jobSummary({
        id: 'a',
        kind: 'audio',
        label: 'Audio',
        state: 'running',
        queued: 12,
        rate: '3.1 s/para',
        etaSeconds: 38,
      }),
    ).toEqual(['12 queued', '3.1 s/para', 'ETA 0:38']);
  });

  it('omits ETA when not running and falls back to detail', () => {
    expect(
      jobSummary({
        id: 'a',
        kind: 'assembly',
        label: 'Assembly',
        state: 'done',
        etaSeconds: 3,
        detail: 'Encoded',
      }),
    ).toEqual(['Encoded']);
  });
});

describe('r2m-job-pill', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders label, summary and busy state', async () => {
    const fixture = await mount();
    const el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-pill')!;
    expect(el.classList.contains('r2m-job-pill--busy')).toBe(true);
    expect(el.querySelector('.r2m-job-pill__label')?.textContent).toBe('Attributing');
    expect(el.querySelector('.r2m-job-pill__summary')?.textContent).toBe(
      '12 queued · 3.1 s/para · ETA 0:38',
    );
    expect(el.querySelector('mat-progress-spinner')).not.toBeNull();
  });

  it('emits open and cancel with the job id', async () => {
    const fixture = await mount();
    const el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-pill')!;
    (el.querySelector('.r2m-job-pill__main') as HTMLButtonElement).click();
    (el.querySelector('.r2m-job-pill__cancel') as HTMLButtonElement).click();
    expect(fixture.componentInstance.opened).toBe('attr');
    expect(fixture.componentInstance.cancelled).toBe('attr');
  });

  it('hides the cancel button and shows an icon when done', async () => {
    const fixture = await mount();
    fixture.componentInstance.job.set({
      id: 'x',
      kind: 'assembly',
      label: 'Assembly',
      state: 'done',
    });
    await fixture.whenStable();
    const el = (fixture.nativeElement as HTMLElement).querySelector('r2m-job-pill')!;
    expect(el.classList.contains('r2m-job-pill--ok')).toBe(true);
    expect(el.querySelector('.r2m-job-pill__cancel')).toBeNull();
    expect(el.querySelector('mat-icon')?.textContent).toBe('library_music');
  });
});
