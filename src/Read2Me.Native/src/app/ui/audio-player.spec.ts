import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import './audio-player';
import { type AbSide, type AudioPlayer, formatClock } from './audio-player';

/** Ported from the Angular TestBed spec; happy-dom's media element plays nothing, so play/pause are stubbed. */
const proto = HTMLMediaElement.prototype as unknown as {
  play: () => Promise<void>;
  pause: () => void;
};
const original = { play: proto.play, pause: proto.pause };
let played = 0;
let paused = 0;

beforeEach(() => {
  played = 0;
  paused = 0;
  proto.play = () => {
    played++;
    return Promise.resolve();
  };
  proto.pause = () => void paused++;
});
afterEach(() => {
  proto.play = original.play;
  proto.pause = original.pause;
  document.body.replaceChildren();
});

async function mount(props: Partial<AudioPlayer> = {}) {
  const player = Object.assign(document.createElement('r2m-audio-player'), {
    src: '/workspace/foundation/item-1.wav',
    label: 'Hardin',
    ...props,
  });
  document.body.append(player);
  await player.rendered();
  return { player, audio: player.querySelector('audio')! };
}

function setDuration(audio: HTMLAudioElement, seconds: number) {
  Object.defineProperty(audio, 'duration', { value: seconds, configurable: true });
  audio.dispatchEvent(new Event('loadedmetadata'));
}

const text = (el: Element, selector: string) => el.querySelector(selector)?.textContent?.trim();

describe('r2m-audio-player', () => {
  it('formats the clock', () => {
    expect(formatClock(null)).toBe('–:––');
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(75.9)).toBe('1:15');
  });

  it('shows the label, an unknown duration until metadata loads, then m:ss / m:ss', async () => {
    const { player, audio } = await mount();
    expect(text(player, '.r2m-audio-player__label')).toBe('Hardin');
    expect(text(player, '.r2m-audio-player__time')).toBe('0:00 / –:––');
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.wav');

    setDuration(audio, 125);
    await player.rendered();
    expect(text(player, '.r2m-audio-player__time')).toBe('0:00 / 2:05');

    audio.currentTime = 30;
    audio.dispatchEvent(new Event('timeupdate'));
    await player.rendered();
    expect(text(player, '.r2m-audio-player__time')).toBe('0:30 / 2:05');
  });

  it('toggles play/pause and reports ended', async () => {
    const { player, audio } = await mount();
    let ended = 0;
    player.addEventListener('playback-ended', () => ended++);
    const button = player.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!;

    button.click();
    expect(played).toBe(1);
    audio.dispatchEvent(new Event('play'));
    await player.rendered();
    expect(button.getAttribute('aria-label')).toBe('Pause');

    button.click();
    expect(paused).toBe(1);
    audio.dispatchEvent(new Event('pause'));
    await player.rendered();
    expect(button.getAttribute('aria-label')).toBe('Play');

    audio.dispatchEvent(new Event('ended'));
    expect(ended).toBe(1);
  });

  it('appends the cache buster to the source', async () => {
    const { player, audio } = await mount();
    player.cacheKey = 7;
    await player.rendered();
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.wav?v=7');
  });

  it('shows the error state when the source cannot play', async () => {
    const { player, audio } = await mount();
    audio.dispatchEvent(new Event('error'));
    await player.rendered();
    expect(text(player, '.r2m-audio-player__error')).toBe("Can't play");
    expect(text(player, '.r2m-audio-player__toggle .r2m-icon')).toBe('warning');
    expect(player.querySelector('.r2m-audio-player__scrub')).toBeNull();
    expect(player.classList.contains('r2m-audio-player--error')).toBe(true);
  });

  it('A/B toggle swaps the source and restores position and play state', async () => {
    const { player, audio } = await mount({ srcB: '/workspace/foundation/item-1.source.wav' });
    const sides: AbSide[] = [];
    player.addEventListener('ab', (e) => sides.push((e as CustomEvent<AbSide>).detail));
    setDuration(audio, 100);
    audio.currentTime = 42;
    audio.dispatchEvent(new Event('play'));
    await player.rendered();

    const buttons = player.querySelectorAll<HTMLButtonElement>('.r2m-audio-player__ab-btn');
    expect(buttons.length).toBe(2);
    expect(buttons[0]!.textContent?.trim()).toBe('A');
    buttons[1]!.click();
    await player.rendered();

    expect(sides).toEqual(['b']);
    expect(audio.getAttribute('src')).toBe('/workspace/foundation/item-1.source.wav');
    expect(buttons[1]!.getAttribute('aria-pressed')).toBe('true');

    setDuration(audio, 100);
    await player.rendered();
    expect(audio.currentTime).toBe(42);
    expect(played).toBe(1);
  });
});
