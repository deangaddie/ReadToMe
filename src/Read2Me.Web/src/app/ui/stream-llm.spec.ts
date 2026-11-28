import { afterEach, describe, expect, it } from 'bun:test';
import type { LlmStreamEvent } from '@app/live/hub-events';
import { fakeScroller } from '../../testing/fake-scroller';
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

const control = (el: StreamLlm) => el.querySelector<HTMLButtonElement>('.r2m-stream-llm__follow');

/** One more event, which makes the content 500 px taller (the first call: 1000 px). */
async function grow(el: StreamLlm, view: ReturnType<typeof fakeScroller>) {
  view.height += view.height ? 500 : 1000;
  el.events = [...el.events, { kind: 'runStarted' }];
  await el.rendered();
}

describe('r2m-stream-llm', () => {
  it('shows an empty message, then turn cards with markers and stats', async () => {
    const el = await mount();
    expect(el.querySelector('.r2m-stream-llm__empty')).not.toBeNull();

    el.events = [
      { kind: 'runStarted' },
      {
        kind: 'requestStarted',
        paragraphPreview: 'Elizabeth leaned back',
        prompt: 'THE PROMPT',
        configId: 'c',
        configName: 'gemma-26b',
      },
      { kind: 'thinkingDelta', text: 'hmm' },
      { kind: 'contentDelta', text: '{"speaker":"Elizabeth"}' },
      { kind: 'streamCompleted', tokensOut: 12, generationMs: 1234, tokensPerSecond: 9.72 },
    ];
    await el.rendered();

    expect(el.querySelector('.r2m-stream-llm__marker')?.textContent).toContain('Run started');
    const turn = el.querySelector('.r2m-stream-llm__turn')!;
    expect(turn.getAttribute('data-state')).toBe('completed');
    expect(turn.querySelector('.r2m-stream-llm__preview')?.textContent).toBe(
      'Elizabeth leaned back',
    );
    expect(turn.querySelector('.r2m-stream-llm__config')?.textContent).toBe('gemma-26b');
    expect(turn.querySelector('.r2m-status-chip')?.textContent).toContain('Completed');
    expect(turn.querySelector('.r2m-stream-llm__stats')?.textContent).toBe(
      '12 tok · 9.7 tok/s · 1.2 s',
    );
    expect(turn.querySelector('.r2m-stream-llm__response')?.textContent).toBe(
      '{"speaker":"Elizabeth"}',
    );
    expect(turn.querySelector('.r2m-stream-llm__section--thinking pre')?.textContent).toBe('hmm');
    expect(turn.querySelector('.r2m-stream-llm__section pre')?.textContent).toBe('THE PROMPT');
  });

  it('offers one scrolling control once there is an entry, saying what a press does', async () => {
    const el = await mount();
    expect(control(el)).toBeNull();

    el.events = [{ kind: 'runStarted' }];
    await el.rendered();

    expect(control(el)?.textContent).toContain('Pause scrolling');
    expect(control(el)?.hasAttribute('aria-pressed')).toBe(false);
    expect(el.querySelectorAll('button').length).toBe(1);
  });

  it('holds still while paused by the control and resumes at the bottom', async () => {
    const el = await mount([{ kind: 'runStarted' }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);
    await grow(el, view);
    expect(view.el.scrollTop).toBe(800);

    control(el)!.click();
    await el.rendered();
    expect(el.paused()).toBe(true);
    expect(control(el)?.textContent).toContain('Jump to latest');
    // The browser's late event for the element's own last scroll must not undo the press.
    view.el.dispatchEvent(new Event('scroll'));
    expect(el.paused()).toBe(true);

    await grow(el, view);
    expect(view.el.scrollTop).toBe(800);

    control(el)!.click();
    await el.rendered();
    expect(el.paused()).toBe(false);
    expect(view.el.scrollTop).toBe(1300);
    expect(control(el)?.textContent).toContain('Pause scrolling');
  });

  it('pauses when the user scrolls up and resumes when they return to the bottom', async () => {
    const el = await mount([{ kind: 'runStarted' }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);
    await grow(el, view);

    view.userScrollsTo(300);
    await el.rendered();
    expect(el.paused()).toBe(true);
    expect(control(el)?.textContent).toContain('Jump to latest');

    view.userScrollsTo(800); // the bottom of 1000 px
    await el.rendered();
    expect(el.paused()).toBe(false);
    expect(control(el)?.textContent).toContain('Pause scrolling');
  });

  it('resumes when content shrinks around a user who scrolled up, leaving them at the bottom', async () => {
    const el = await mount([{ kind: 'runStarted' }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);
    await grow(el, view);
    view.userScrollsTo(300);

    view.height = 400;
    view.el.dispatchEvent(new Event('scroll'));

    expect(el.paused()).toBe(false);
  });

  it('leaves a scroll up for its own event when a turn changes size first', async () => {
    const el = await mount([{ kind: 'runStarted' }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);
    await grow(el, view);

    view.el.scrollTop = 300;
    view.el.firstElementChild!.dispatchEvent(new Event('contentvisibilityautostatechange'));
    expect(view.el.scrollTop).toBe(300);
    view.el.dispatchEvent(new Event('scroll'));

    expect(el.paused()).toBe(true);
  });

  it('keeps following when turns take their real size below the view', async () => {
    const el = await mount([{ kind: 'runStarted' }]);
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);
    await grow(el, view);

    view.height = 1400;
    // A scroll event that finds the view short of the bottom without having moved up.
    view.el.dispatchEvent(new Event('scroll'));
    expect(el.paused()).toBe(false);
    view.el.firstElementChild!.dispatchEvent(new Event('contentvisibilityautostatechange'));
    expect(view.el.scrollTop).toBe(1200);

    control(el)!.click();
    view.height = 1800;
    view.el.firstElementChild!.dispatchEvent(new Event('contentvisibilityautostatechange'));
    expect(view.el.scrollTop).toBe(1200);
  });

  it('leaves a hidden list alone and lands at the latest entry when shown, unless paused', async () => {
    const el = await mount();
    const view = fakeScroller(el.querySelector<HTMLElement>('.r2m-stream-llm__scroll')!);

    el.shown = false;
    view.height = 1000;
    view.el.dispatchEvent(new Event('scroll'));
    el.events = [{ kind: 'runStarted' }];
    await el.rendered();
    expect(el.paused()).toBe(false);
    expect(view.el.scrollTop).toBe(0);

    el.shown = true;
    expect(view.el.scrollTop).toBe(800);

    view.userScrollsTo(40);
    el.shown = false;
    el.shown = true;
    expect(view.el.scrollTop).toBe(40);
  });
});
