import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AudioGenEvent } from '@app/live/hub-events';
import { StreamAudio } from './stream-audio';

@Component({
  imports: [StreamAudio],
  template: `<r2m-stream-audio [events]="events()" />`,
})
class HostCmp {
  readonly events = signal<AudioGenEvent[]>([]);
}

describe('r2m-stream-audio', () => {
  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    return fixture;
  }

  it('renders a card per attempt with five phases and their lines', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.r2m-stream-audio__empty')).not.toBeNull();

    fixture.componentInstance.events.set([
      { kind: 'itemStarted', id: 'i1', attempt: 1, character: 'Hardin', text: 'Hello there.' },
      { kind: 'audioGenerated', id: 'i1', attempt: 1 },
      { kind: 'normalized', id: 'i1', attempt: 1, ok: true },
      { kind: 'postProcessed', id: 'i1', attempt: 1, stepId: 'silence-trim', applied: true },
      { kind: 'itemStarted', id: 'i2', attempt: 1, character: 'Pirenne' },
      { kind: 'failed', id: 'i2', attempt: 1, reason: 'TTS 500' },
    ]);
    await fixture.whenStable();

    const cards = Array.from(el.querySelectorAll('.r2m-stream-audio__card'));
    expect(cards.length).toBe(2);
    const first = cards[0]!;
    expect(first.querySelector('.r2m-stream-audio__character')?.textContent).toBe('Hardin');
    expect(first.querySelector('.r2m-stream-audio__text')?.textContent).toBe('Hello there.');
    expect(first.querySelector('r2m-status-chip')?.textContent).toContain('Running');
    const states = Array.from(first.querySelectorAll('.r2m-stream-audio__phase')).map((p) =>
      p.getAttribute('data-state'),
    );
    expect(states).toEqual(['ok', 'ok', 'ok', 'active', 'pending']);
    expect(first.querySelector('.r2m-stream-audio__lines li')?.textContent).toBe(
      'silence-trim: applied',
    );
    expect(first.querySelectorAll('.r2m-stream-audio__phase mat-progress-spinner').length).toBe(1);

    const second = cards[1]!;
    expect(second.classList.contains('r2m-stream-audio__card--failed')).toBe(true);
    expect(second.querySelector('r2m-status-chip')?.textContent).toContain('Failed');
    expect(second.querySelector('.r2m-stream-audio__reason')?.textContent).toBe('TTS 500');
  });

  it('pauses autoscroll when scrolled up and resumes on Jump to latest', async () => {
    const fixture = await mount();
    const el = fixture.nativeElement as HTMLElement;
    const scroll = el.querySelector('.r2m-stream-audio__scroll') as HTMLElement;
    Object.defineProperty(scroll, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(scroll, 'clientHeight', { value: 200, configurable: true });
    scroll.scrollTop = 0;
    scroll.dispatchEvent(new Event('scroll'));
    await fixture.whenStable();

    const cmp = fixture.debugElement.query((d) => d.name === 'r2m-stream-audio')
      .componentInstance as StreamAudio;
    expect(cmp.paused()).toBe(true);
    (el.querySelector('.r2m-stream-audio__jump') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(cmp.paused()).toBe(false);
  });
});
