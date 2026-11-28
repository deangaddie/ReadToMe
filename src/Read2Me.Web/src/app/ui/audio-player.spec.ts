import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { Block } from '@app/api';
import { html, nothing, render } from 'lit-html';
import './audio-player';
import { type AbSide, type AudioPlayer, formatClock } from './audio-player';
import { gated } from './partials';
import { blockedWith } from '../../testing/description';
import { fakeIntersection } from '../../testing/fake-intersection';

/** Ported from the Angular TestBed spec; happy-dom's media element plays nothing, so play/pause are stubbed. */
const proto = HTMLMediaElement.prototype as unknown as {
  play: () => Promise<void>;
  pause: () => void;
};
const original = { play: proto.play, pause: proto.pause };
let played = 0;
let paused = 0;
const viewport = fakeIntersection();
let restoreViewport = () => {};

beforeEach(() => {
  played = 0;
  paused = 0;
  restoreViewport = viewport.install();
  proto.play = () => {
    played++;
    return Promise.resolve();
  };
  proto.pause = () => void paused++;
});
afterEach(() => {
  proto.play = original.play;
  proto.pause = original.pause;
  restoreViewport();
  document.body.replaceChildren();
});

async function mount(props: Partial<AudioPlayer> = {}) {
  const player = Object.assign(document.createElement('r2m-audio-player'), {
    src: '/workspace/pride-and-prejudice/item-1.wav',
    label: 'Elizabeth',
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
  it('behind an Edit gate with a reason, keeps its play button focusable and says why', async () => {
    const host = document.createElement('div');
    document.body.append(host);
    const draw = async (block: Block | null) => {
      render(
        gated(
          block,
          (gate) =>
            html`<r2m-audio-player
              .src=${gate.blocked ? null : '/workspace/dracula/audio/d.wav'}
              .gate=${gate}
            ></r2m-audio-player>`,
        ),
        host,
      );
      await host.querySelector('r2m-audio-player')!.rendered();
    };
    const button = () => host.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!;

    await draw({ reason: "Item audio isn't kept in History" });
    const first = button();
    expect(blockedWith(first)).toBe("Item audio isn't kept in History");
    expect(first.disabled).toBe(false);
    expect(first.getAttribute('aria-label')).toBe('Play');
    first.click();
    expect(played).toBe(0);

    await draw({ reason: null });
    expect([button().disabled, blockedWith(button())]).toEqual([true, null]);

    await draw(null);
    expect(button()).toBe(first);
    expect([button().disabled, blockedWith(button())]).toEqual([false, null]);
    button().click();
    expect(played).toBe(1);
  });

  it('is plain disabled from its first render when blocked without a reason', async () => {
    const host = document.createElement('div');
    document.body.append(host);
    for (const src of ['/workspace/dracula/audio/d.wav', null]) {
      document.body.replaceChildren(host);
      render(
        gated(
          { reason: null },
          (gate) => html`<r2m-audio-player .src=${src} .gate=${gate}></r2m-audio-player>`,
        ),
        host,
      );
      await host.querySelector('r2m-audio-player')!.rendered();
      expect(host.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!.disabled).toBe(
        true,
      );
      render(nothing, host);
    }
  });

  it('is off without a source once its gate is open', async () => {
    const host = document.createElement('div');
    document.body.append(host);
    for (const block of [{ reason: 'Not yet' }, null]) {
      render(
        gated(block, (gate) => html`<r2m-audio-player .gate=${gate}></r2m-audio-player>`),
        host,
      );
      await host.querySelector('r2m-audio-player')!.rendered();
    }
    expect(host.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!.disabled).toBe(true);
  });

  it('reads no metadata until it nears the viewport, then stops watching', async () => {
    const { player, audio } = await mount();
    expect(audio.getAttribute('preload')).toBe('none');
    expect(audio.getAttribute('src')).toBe('/workspace/pride-and-prejudice/item-1.wav');
    expect(viewport.options).toEqual({ rootMargin: '200px', scrollMargin: '200px' });

    viewport.enter(player, false);
    await player.rendered();
    expect(audio.getAttribute('preload')).toBe('none');

    viewport.enter(player);
    await player.rendered();
    expect(audio.getAttribute('preload')).toBe('metadata');
    expect(viewport.watched(player)).toBe(false);
  });

  it('stops watching when it leaves the page before nearing the viewport', async () => {
    const { player } = await mount();
    expect(viewport.watched(player)).toBe(true);
    player.remove();
    expect(viewport.watched(player)).toBe(false);
  });

  it('plays before it has neared the viewport, and is near from then on', async () => {
    const { player, audio } = await mount();
    player.querySelector<HTMLButtonElement>('.r2m-audio-player__toggle')!.click();
    expect(played).toBe(1);
    await player.rendered();
    expect(audio.getAttribute('preload')).toBe('metadata');
    expect(viewport.watched(player)).toBe(false);
  });

  it("reads the other side's metadata when A/B is switched before it neared the viewport", async () => {
    const { player, audio } = await mount({
      srcB: '/workspace/pride-and-prejudice/item-1.source.wav',
    });
    player.switchSide('b');
    await player.rendered();
    expect(audio.getAttribute('preload')).toBe('metadata');
  });

  it('formats the clock', () => {
    expect(formatClock(null)).toBe('–:––');
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(75.9)).toBe('1:15');
  });

  it('shows the label, an unknown duration until metadata loads, then m:ss / m:ss', async () => {
    const { player, audio } = await mount();
    expect(text(player, '.r2m-audio-player__label')).toBe('Elizabeth');
    expect(text(player, '.r2m-audio-player__time')).toBe('0:00 / –:––');
    expect(audio.getAttribute('src')).toBe('/workspace/pride-and-prejudice/item-1.wav');

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
    expect(audio.getAttribute('src')).toBe('/workspace/pride-and-prejudice/item-1.wav?v=7');
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
    const { player, audio } = await mount({
      srcB: '/workspace/pride-and-prejudice/item-1.source.wav',
    });
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
    expect(audio.getAttribute('src')).toBe('/workspace/pride-and-prejudice/item-1.source.wav');
    expect(buttons[1]!.getAttribute('aria-pressed')).toBe('true');

    setDuration(audio, 100);
    await player.rendered();
    expect(audio.currentTime).toBe(42);
    expect(played).toBe(1);
  });
});
