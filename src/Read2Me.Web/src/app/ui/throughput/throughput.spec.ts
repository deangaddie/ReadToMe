import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ThroughputSnapshot } from '@app/live/live-messages';
import { Throughput } from './throughput';

@Component({
  imports: [Throughput],
  template: `<r2m-throughput [snapshot]="snapshot()" />`,
})
class HostCmp {
  readonly snapshot = signal<ThroughputSnapshot>({
    hasRun: true,
    isRunActive: true,
    runThroughput: null,
    generationRate: 41.26,
    generationRateHistory: [null, 10, 20],
    perConfig: [{ configId: 1, configName: 'small', requests: 1 }],
  });
}

describe('r2m-throughput', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the live rate and no table while the run is active', async () => {
    const { el } = await mount();

    expect(el.querySelector('.r2m-throughput__headline')?.textContent).toBe('41.3 tok/s now');
    expect(el.querySelector('r2m-sparkline')).not.toBeNull();
    expect(el.querySelector('.r2m-throughput__table')).toBeNull();
  });

  it('shows the run rate and the per-config table once it has ended', async () => {
    const { fixture, el } = await mount();
    fixture.componentInstance.snapshot.update((s) => ({
      ...s,
      isRunActive: false,
      runThroughput: 30,
      generationRate: null,
      perConfig: [
        {
          configId: 1,
          configName: 'small',
          requests: 3,
          tokensOut: 900,
          generationMs: 30000,
          tokensPerSecond: 30,
        },
        { configId: 2, configName: 'big', requests: 1 },
      ],
    }));
    await fixture.whenStable();

    expect(el.querySelector('.r2m-throughput__headline')?.textContent).toBe(
      '30.0 tok/s over the run',
    );
    const rows = Array.from(el.querySelectorAll('.r2m-throughput__table tbody tr')).map((r) =>
      Array.from(r.querySelectorAll('td')).map((c) => c.textContent?.trim()),
    );
    expect(rows).toEqual([
      ['small', '3', '900', '30.0 s', '30.0'],
      ['big', '1', '–', '–', '–'],
    ]);
  });
});
