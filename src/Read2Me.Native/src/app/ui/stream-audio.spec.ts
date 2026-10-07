import { afterEach, describe, expect, it } from 'bun:test';
import type { AudioGenEvent } from '@app/live/hub-events';
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

describe('r2m-stream-audio', () => {
  it('renders a card per attempt with five phases and their lines', async () => {
    const el = await mount();
    expect(el.querySelector('.r2m-stream-audio__empty')).not.toBeNull();

    el.events = [
      { kind: 'itemStarted', id: 'i1', attempt: 1, character: 'Hardin', text: 'Hello there.' },
      { kind: 'audioGenerated', id: 'i1', attempt: 1 },
      { kind: 'normalized', id: 'i1', attempt: 1, ok: true },
      { kind: 'postProcessed', id: 'i1', attempt: 1, stepId: 'silence-trim', applied: true },
      { kind: 'itemStarted', id: 'i2', attempt: 1, character: 'Pirenne' },
      { kind: 'failed', id: 'i2', attempt: 1, reason: 'TTS 500' },
    ];
    await el.rendered();

    const cards = Array.from(el.querySelectorAll('.r2m-stream-audio__card'));
    expect(cards.length).toBe(2);
    const first = cards[0]!;
    expect(first.querySelector('.r2m-stream-audio__character')?.textContent).toBe('Hardin');
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

  it('pauses autoscroll when scrolled up and resumes on Jump to latest', async () => {
    const el = await mount();
    const scroll = el.querySelector('.r2m-stream-audio__scroll') as HTMLElement;
    Object.defineProperty(scroll, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(scroll, 'clientHeight', { value: 200, configurable: true });
    scroll.scrollTop = 0;
    scroll.dispatchEvent(new Event('scroll'));
    await el.rendered();

    expect(el.paused()).toBe(true);
    el.querySelector<HTMLButtonElement>('.r2m-stream-audio__jump')!.click();
    await el.rendered();
    expect(el.paused()).toBe(false);
  });
});
