import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AbSide, AudioPlayer, formatClock } from './audio-player';

@Component({
  imports: [AudioPlayer],
  template: `
    <r2m-audio-player
      [src]="src()"
      [srcB]="srcB()"
      [cacheKey]="cacheKey()"
      label="Hardin"
      (playbackEnded)="endedCount = endedCount + 1"
      (ab)="sides.push($event)"
    />
  `,
})
class HostCmp {
  readonly src = signal<string | null>('/workspace/foundation/item-1.wav');
  readonly srcB = signal<string | null>(null);
  readonly cacheKey = signal<string | number | undefined>(undefined);
  endedCount = 0;
  sides: AbSide[] = [];
}

describe('r2m-audio-player', () => {
  let play: () => void;
  let pause: () => void;

  beforeEach(() => {
    play = vi.fn();
    pause = vi.fn();
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(() => {
      play();
      return Promise.resolve();
    });
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {
      pause();
    });
  });

  afterEach(() => vi.restoreAllMocks());

  async function mount() {
    await TestBed.configureTestingModule({ imports: [HostCmp] }).compileComponents();
    const fixture = TestBed.createComponent(HostCmp);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const audio = el.querySelector('audio')!;
    return { fixture, el, audio };
  }

  function setDuration(audio: HTMLAudioElement, seconds: number) {
    Object.defineProperty(audio, 'duration', { value: seconds, configurable: true });
    audio.dispatchEvent(new Event('loadedmetadata'));
  }

  it('formats the clock', () => {
    expect(formatClock(null)).toBe('–:––');
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(75.9)).toBe('1:15');
  });

  it('shows the label, an unknown duration until metadata loads, then m:ss / m:ss', async () => {
    const { fixture, el, audio } = await mount();
    expect(el.querySelector('.r2m-audio-player__label')?.textContent).toBe('Hardin');
    expect(el.querySelector('.r2m-audio-player__time')?.textContent).toBe('0:00 / –:––');
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.wav');

    setDuration(audio, 125);
    await fixture.whenStable();
    expect(el.querySelector('.r2m-audio-player__time')?.textContent).toBe('0:00 / 2:05');

    audio.currentTime = 30;
    audio.dispatchEvent(new Event('timeupdate'));
    await fixture.whenStable();
    expect(el.querySelector('.r2m-audio-player__time')?.textContent).toBe('0:30 / 2:05');
  });

  it('toggles play/pause and reports ended', async () => {
    const { fixture, el, audio } = await mount();
    const button = el.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!;

    button.click();
    expect(play).toHaveBeenCalledTimes(1);
    audio.dispatchEvent(new Event('play'));
    await fixture.whenStable();
    expect(button.getAttribute('aria-label')).toBe('Pause');

    button.click();
    expect(pause).toHaveBeenCalledTimes(1);
    audio.dispatchEvent(new Event('pause'));
    await fixture.whenStable();
    expect(button.getAttribute('aria-label')).toBe('Play');

    audio.dispatchEvent(new Event('ended'));
    expect(fixture.componentInstance.endedCount).toBe(1);
  });

  it('appends the cache buster to the source', async () => {
    const { fixture, audio } = await mount();
    fixture.componentInstance.cacheKey.set(7);
    await fixture.whenStable();
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.wav?v=7');
  });

  it('shows the error state when the source cannot play', async () => {
    const { fixture, el, audio } = await mount();
    audio.dispatchEvent(new Event('error'));
    await fixture.whenStable();
    expect(el.querySelector('.r2m-audio-player__error')?.textContent).toBe("Can't play");
    expect(el.querySelector('.r2m-audio-player__toggle mat-icon')?.textContent).toBe('warning');
    expect(el.querySelector('.r2m-audio-player__scrub')).toBeNull();
  });

  it('A/B toggle swaps the source and restores position and play state', async () => {
    const { fixture, el, audio } = await mount();
    fixture.componentInstance.srcB.set('/workspace/foundation/item-1.source.wav');
    await fixture.whenStable();
    setDuration(audio, 100);
    audio.currentTime = 42;
    audio.dispatchEvent(new Event('play'));
    await fixture.whenStable();

    const buttons = el.querySelectorAll<HTMLButtonElement>('.r2m-audio-player__ab-btn');
    expect(buttons.length).toBe(2);
    expect(buttons[0]!.textContent?.trim()).toBe('A');
    buttons[1]!.click();
    await fixture.whenStable();

    expect(fixture.componentInstance.sides).toEqual(['b']);
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.source.wav');
    expect(buttons[1]!.getAttribute('aria-pressed')).toBe('true');

    setDuration(audio, 100);
    await fixture.whenStable();
    expect(audio.currentTime).toBe(42);
    expect(play).toHaveBeenCalledTimes(1);
  });
});
