import { afterEach, describe, expect, it } from 'bun:test';
import type { LlmStreamEvent } from '@app/live/hub-events';
import './stream-llm';
import type { StreamLlm } from './stream-llm';

/** Ported from the Angular r2m-stream-llm TestBed spec, case for case. */
async function mount(events: LlmStreamEvent[] = []) {
  const el = document.createElement('r2m-stream-llm') as StreamLlm;
  el.events = events;
  document.body.append(el);
  await el.rendered();
  return el;
}

afterEach(() => document.body.replaceChildren());

describe('r2m-stream-llm', () => {
  it('shows an empty message, then turn cards with markers and stats', async () => {
    const el = await mount();
    expect(el.querySelector('.r2m-stream-llm__empty')).not.toBeNull();

    el.events = [
      { kind: 'runStarted' },
      {
        kind: 'requestStarted',
        paragraphPreview: 'Hardin leaned back',
        prompt: 'THE PROMPT',
        configId: 'c',
        configName: 'gemma-26b',
      },
      { kind: 'thinkingDelta', text: 'hmm' },
      { kind: 'contentDelta', text: '{"speaker":"Hardin"}' },
      { kind: 'streamCompleted', tokensOut: 12, generationMs: 1234, tokensPerSecond: 9.72 },
    ];
    await el.rendered();

    expect(el.querySelector('.r2m-stream-llm__marker')?.textContent).toContain('Run started');
    const turn = el.querySelector('.r2m-stream-llm__turn')!;
    expect(turn.getAttribute('data-state')).toBe('completed');
    expect(turn.querySelector('.r2m-stream-llm__preview')?.textContent).toBe('Hardin leaned back');
    expect(turn.querySelector('.r2m-stream-llm__config')?.textContent).toBe('gemma-26b');
    expect(turn.querySelector('.r2m-status-chip')?.textContent).toContain('Completed');
    expect(turn.querySelector('.r2m-stream-llm__stats')?.textContent).toBe(
      '12 tok · 9.7 tok/s · 1.2 s',
    );
    expect(turn.querySelector('.r2m-stream-llm__response')?.textContent).toBe(
      '{"speaker":"Hardin"}',
    );
    expect(turn.querySelector('.r2m-stream-llm__section--thinking pre')?.textContent).toBe('hmm');
    expect(turn.querySelector('.r2m-stream-llm__section pre')?.textContent).toBe('THE PROMPT');
  });

  it('pauses autoscroll when scrolled up and resumes on Jump to latest', async () => {
    const el = await mount();
    const scroll = el.querySelector('.r2m-stream-llm__scroll') as HTMLElement;
    // happy-dom has no layout: fake a tall content area scrolled to the top.
    Object.defineProperty(scroll, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(scroll, 'clientHeight', { value: 200, configurable: true });
    scroll.scrollTop = 0;
    scroll.dispatchEvent(new Event('scroll'));
    await el.rendered();

    expect(el.paused()).toBe(true);
    const jump = el.querySelector<HTMLButtonElement>('.r2m-stream-llm__jump');
    expect(jump).not.toBeNull();

    jump!.click();
    await el.rendered();
    expect(el.paused()).toBe(false);
    expect(el.querySelector('.r2m-stream-llm__jump')).toBeNull();
  });
});
