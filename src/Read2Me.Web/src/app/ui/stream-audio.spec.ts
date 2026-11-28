import { afterEach, describe, expect, it } from 'bun:test';
import type { AudioGenEvent } from '@app/live/hub-events';
import { fakeScroller } from '../../testing/fake-scroller';
import './stream-audio';
import type { StreamAudio } from './stream-audio';

/** Ported from the Angular r2m-stream-audio TestBed spec, case for case. */
async function mount(events: AudioGenEvent[] = []) {
  const el = document.createElement('r2m-stream-audio') as StreamAudio;
  el.events = events;
  document.body.append(el);
  await el.rendered();
  return el;
}

afterEach(() => document.body.replaceChildren());

const control = (el: StreamAudio) =>
  el.querySelector<HTMLButtonElement>('.r2m-stream-audio__follow');

/** One more card, which makes the content 500 px taller (the first call: 1000 px). */
async function grow(el: StreamAudio, view: ReturnType<typeof fakeScroller>) {
  view.height += view.height ? 500 : 1000;
  const id = `i${el.events.length + 1}`;
  el.events = [...el.events, { kind: 'itemStarted', id, attempt: 1 }];
  await el.rendered();
}

describe('r2m-stream-audio', () => {
  it('renders a card per attempt with five phases and their lines', async () => {
    const el = await mount();
    expect(el.querySelector('.r2m-stream-audio__empty')).not.toBeNull();

    el.events = [
      { kind: 'itemStarted', id: 'i1', attempt: 1, character: 'Elizabeth', text: 'Hello there.' },
      { kind: 'audioGenerated', id: 'i1', attempt: 1 },
      { kind: 'normalized', id: 'i1', attempt: 1, ok: true },
      { kind: 'postProcessed', id: 'i1', attempt: 1, stepId: 'silence-trim', applied: true },
      { kind: 'itemStarted', id: 'i2', attempt: 1, character: 'Bingley' },
      { kind: 'failed', id: 'i2', attempt: 1, reason: 'TTS 500' },
    ];
    await el.rendered();

    const cards = Array.from(el.querySelectorAll('.r2m-stream-audio__card'));
    expect(cards.length).toBe(2);
    const first = cards[0]!;
    expect(first.querySelector('.r2m-stream-audio__character')?.textContent).toBe('Elizabeth');
    expect(first.querySelector('.r2m-stream-audio__text')?.textContent).toBe('Hello there.');
    expect(first.querySelector('.r2m-status-chip')?.textContent).toContain('Running');
    const states = Array.from(first.querySelectorAll('.r2m-stream-audio__phase')).map((p) =>
      p.getAttribute('data-state'),
    );
    expect(states).toEqual(['ok', 'ok', 'ok', 'active', 'pending']);
    expect(first.querySelector('.r2m-stream-audio__lines li')?.textContent).toBe(
      'silence-trim: applied',
    );
    expect(first.querySelectorAll('.r2m-stream-audio__phase .r2m-spinner').length).toBe(1);

    const second = cards[1]!;
    expect(second.classList.contains('r2m-stream-audio__card--failed')).toBe(true);
    expect(second.querySelector('.r2m-status-chip')?.textContent).toContain('Failed');
    expect(second.querySelector('.r2m-stream-audio__reason')?.textContent).toBe('TTS 500');
  });

  it('offers one scrolling control once there is an entry', async () => {
    const el = await mount();
    expect(el.querySelector('button')).toBeNull();

    el.events = [{ kind: 'itemStarted', id: 'i1', attempt: 1 }];
    await el.rendered();

    expect(el.querySelectorAll('button').length).toBe(1);
    expect(control(el)?.textContent).toContain('Pause scrolling');
    expect(control(el)?.hasAttribute('aria-pressed')).toBe(false);
  });

  it('holds still while paused by the control and resumes at the bottom', async () => {
    const el = await mount([{ kind: 'itemStarted', id: 'i1', attempt: 1 }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-audio__scroll')!);
    await grow(el, view);

    control(el)!.click();
    await el.rendered();
    expect(control(el)?.textContent).toContain('Jump to latest');
    await grow(el, view);
    expect(view.el.scrollTop).toBe(800);

    control(el)!.click();
    await el.rendered();
    expect(view.el.scrollTop).toBe(1300);
    expect(control(el)?.textContent).toContain('Pause scrolling');
  });

  it('pauses when the user scrolls up and resumes when they return to the bottom', async () => {
    const el = await mount([{ kind: 'itemStarted', id: 'i1', attempt: 1 }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-audio__scroll')!);
    await grow(el, view);

    view.userScrollsTo(300);
    await el.rendered();
    expect(el.paused()).toBe(true);
    expect(control(el)?.textContent).toContain('Jump to latest');

    view.userScrollsTo(800);
    await el.rendered();
    expect(el.paused()).toBe(false);
  });

  it('keeps following when cards take their real size', async () => {
    const el = await mount([{ kind: 'itemStarted', id: 'i1', attempt: 1 }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-audio__scroll')!);
    await grow(el, view);

    view.height = 1400;
    view.el.firstElementChild!.dispatchEvent(new Event('contentvisibilityautostatechange'));
    expect(view.el.scrollTop).toBe(1200);
  });
});
