import { afterEach, describe, expect, it } from 'bun:test';
import { render } from 'lit-html';
import { type JobView, formatDuration, jobSummary } from './job';
import { jobCard, jobPill } from './job-pill';

/** Ported from the Angular TestBed specs for r2m-job-pill and r2m-job-card, case for case. */
function mount(template: unknown): HTMLElement {
  const host = document.createElement('div');
  document.body.append(host);
  render(template, host);
  return host;
}

afterEach(() => document.body.replaceChildren());

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

describe('jobPill', () => {
  const running: JobView = {
    id: 'attr',
    kind: 'attribution',
    label: 'Attributing',
    state: 'running',
    queued: 12,
    rate: '3.1 s/para',
    etaSeconds: 38,
    cancellable: true,
  };

  it('renders label, summary and busy state', () => {
    const host = mount(jobPill({ job: running, onOpen: () => {}, onCancel: () => {} }));
    const el = host.querySelector('.r2m-job-pill')!;
    expect(el.classList.contains('r2m-job-pill--busy')).toBe(true);
    expect(el.querySelector('.r2m-job-pill__label')?.textContent).toBe('Attributing');
    expect(el.querySelector('.r2m-job-pill__summary')?.textContent).toBe(
      '12 queued · 3.1 s/para · ETA 0:38',
    );
    expect(el.querySelector('.r2m-spinner')).not.toBeNull();
  });

  it('emits open and cancel with the job id', () => {
    const log: string[] = [];
    const host = mount(
      jobPill({
        job: running,
        onOpen: (id) => log.push(`open:${id}`),
        onCancel: (id) => log.push(`cancel:${id}`),
      }),
    );
    host.querySelector<HTMLButtonElement>('.r2m-job-pill__main')!.click();
    host.querySelector<HTMLButtonElement>('.r2m-job-pill__cancel')!.click();
    expect(log).toEqual(['open:attr', 'cancel:attr']);
  });

  it('hides the cancel button and shows an icon when done', () => {
    const host = mount(
      jobPill({
        job: { id: 'x', kind: 'assembly', label: 'Assembly', state: 'done' },
        onOpen: () => {},
        onCancel: () => {},
      }),
    );
    const el = host.querySelector('.r2m-job-pill')!;
    expect(el.classList.contains('r2m-job-pill--ok')).toBe(true);
    expect(el.querySelector('.r2m-job-pill__cancel')).toBeNull();
    expect(el.querySelector('.r2m-icon')?.textContent).toBe('library_music');
  });
});

describe('jobCard', () => {
  it('renders numbers, determinate progress, history and actions', () => {
    const log: string[] = [];
    const host = mount(
      jobCard({
        job: {
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
        },
        history: [1, 3, 2, 5],
        onCancel: (id) => log.push(`cancel:${id}`),
        onDismiss: (id) => log.push(`dismiss:${id}`),
      }),
    );
    const el = host.querySelector('.r2m-job-card')!;
    expect(Array.from(el.querySelectorAll('dt')).map((d) => d.textContent)).toEqual([
      'Done',
      'Total',
      'Rate',
      'ETA',
    ]);
    expect(el.querySelector('dd')?.textContent).toBe('25');
    expect(el.querySelector('progress')?.getAttribute('value')).toBe('0.25');
    expect(el.querySelector('svg polyline')?.getAttribute('points')?.split(' ').length).toBe(4);
    expect(el.querySelector('.r2m-status-chip')?.textContent).toContain('Running');

    const buttons = el.querySelectorAll<HTMLButtonElement>('footer button');
    buttons[0]!.click();
    buttons[1]!.click();
    expect(log).toEqual(['cancel:audio', 'dismiss:audio']);
  });

  it('shows indeterminate progress without totals and an error when failed', () => {
    const host = mount(
      jobCard({
        job: { id: 'a', kind: 'assembly', label: 'Assembly', state: 'queued' },
        onCancel: () => {},
        onDismiss: () => {},
      }),
    );
    expect(host.querySelector('progress')?.hasAttribute('value')).toBe(false);

    render(
      jobCard({
        job: {
          id: 'a',
          kind: 'assembly',
          label: 'Assembly',
          state: 'failed',
          error: 'ffmpeg exited 1',
        },
        onCancel: () => {},
        onDismiss: () => {},
      }),
      host,
    );
    expect(host.querySelector('progress')).toBeNull();
    expect(host.querySelector('.r2m-job-card__error')?.textContent).toContain('ffmpeg exited 1');
    expect(host.querySelector('footer')?.children.length).toBe(0);
  });
});
