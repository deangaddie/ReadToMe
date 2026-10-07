import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import type { ThroughputSnapshot } from '@app/live/live-messages';
import { sparkline, sparklinePoints, throughput } from './throughput';

const ACTIVE: ThroughputSnapshot = {
  hasRun: true,
  isRunActive: true,
  runThroughput: null,
  generationRate: 41.26,
  generationRateHistory: [null, 10, 20],
  perConfig: [{ configId: 1, configName: 'small', requests: 1 }],
};

function mount(snapshot: ThroughputSnapshot): HTMLElement {
  const host = document.createElement('div');
  document.body.append(host);
  render(throughput(snapshot), host);
  return host;
}

afterEach(() => document.body.replaceChildren());

describe('sparklinePoints', () => {
  it('scales to min/max with an inset and centres a flat series', () => {
    expect(sparklinePoints([], 96, 24)).toBe('');
    expect(sparklinePoints([5], 96, 24)).toBe('48.0,12.0');
    expect(sparklinePoints([0, 10], 100, 24)).toBe('1.5,22.5 98.5,1.5');
    expect(
      sparklinePoints([3, 3, 3], 100, 24)
        .split(' ')
        .every((p) => p.endsWith(',12.0')),
    ).toBe(true);
  });
});

describe('sparkline()', () => {
  it('draws a labelled svg with one polyline', () => {
    const host = document.createElement('div');
    render(sparkline({ values: [1, 3, 2], width: 96, height: 24, label: 'Tokens/s' }), host);
    const svg = host.querySelector('svg.r2m-sparkline');
    expect(svg?.getAttribute('aria-label')).toBe('Tokens/s');
    expect(svg?.getAttribute('viewBox')).toBe('0 0 96 24');
    expect(svg?.querySelector('polyline')?.getAttribute('points')?.split(' ')).toHaveLength(3);
  });

  it('draws nothing when empty', () => {
    const host = document.createElement('div');
    render(sparkline({ values: [] }), host);
    expect(host.querySelector('polyline')).toBeNull();
  });
});

describe('throughput()', () => {
  it('shows the live rate and no table while the run is active', () => {
    const el = mount(ACTIVE);
    expect(el.querySelector('.r2m-throughput__headline')?.textContent).toBe('41.3 tok/s now');
    expect(
      el.querySelector('.r2m-sparkline polyline')?.getAttribute('points')?.split(' '),
    ).toHaveLength(3);
    expect(el.querySelector('.r2m-throughput__table')).toBeNull();
  });

  it('shows the run rate and the per-config table once it has ended', () => {
    const el = mount({
      ...ACTIVE,
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
    });
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
