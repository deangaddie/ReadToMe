import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { LlmStreamEvent } from '@app/live/hub-events';
import { StreamLlm } from './stream-llm';

@Component({
  imports: [StreamLlm],
  template: `<r2m-stream-llm [events]="events()" />`,
})
class HostCmp {
  readonly events = signal<LlmStreamEvent[]>([]);
}

describe('r2m-stream-llm', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('shows an empty message, then turn cards with markers and stats', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.r2m-stream-llm__empty')).not.toBeNull();

    fixture.componentInstance.events.set([
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
    ]);
    await fixture.whenStable();

    expect(el.querySelector('.r2m-stream-llm__marker')?.textContent).toContain('Run started');
    const turn = el.querySelector('.r2m-stream-llm__turn')!;
    expect(turn.getAttribute('data-state')).toBe('completed');
    expect(turn.querySelector('.r2m-stream-llm__preview')?.textContent).toBe('Hardin leaned back');
    expect(turn.querySelector('.r2m-stream-llm__config')?.textContent).toBe('gemma-26b');
    expect(turn.querySelector('r2m-status-chip')?.textContent).toContain('Completed');
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
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const scroll = el.querySelector('.r2m-stream-llm__scroll') as HTMLElement;
    // jsdom has no layout: fake a tall content area scrolled to the top.
    Object.defineProperty(scroll, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(scroll, 'clientHeight', { value: 200, configurable: true });
    scroll.scrollTop = 0;
    scroll.dispatchEvent(new Event('scroll'));
    await fixture.whenStable();

    const cmp = fixture.debugElement.query((d) => d.name === 'r2m-stream-llm')
      .componentInstance as StreamLlm;
    expect(cmp.paused()).toBe(true);
    const jump = el.querySelector('.r2m-stream-llm__jump') as HTMLButtonElement;
    expect(jump).not.toBeNull();

    jump.click();
    await fixture.whenStable();
    expect(cmp.paused()).toBe(false);
    expect(el.querySelector('.r2m-stream-llm__jump')).toBeNull();
  });
});
