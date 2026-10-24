import { describe, expect, it } from 'bun:test';
import { ApiError, type TryPronunciationRequest } from '@app/api';
import { type PlayableAudio, TryOutPlayer } from './try-out-player';

/** An audio element that sounds until `end()`; records play and pause. */
class FakeAudio implements PlayableAudio {
  played = false;
  paused = false;
  private ended: (() => void)[] = [];
  constructor(readonly url: string) {}
  play() {
    this.played = true;
    return Promise.resolve();
  }
  pause() {
    this.paused = true;
  }
  addEventListener(type: 'ended' | 'error', listener: () => void) {
    if (type === 'ended') this.ended.push(listener);
  }
  end() {
    for (const l of this.ended) l();
  }
}

const request = (itemId: string): TryPronunciationRequest => ({
  itemId,
  sentenceStart: 0,
  draft: null,
  asWritten: false,
  voice: 'item',
});

const flush = () => new Promise((r) => setTimeout(r, 0));

/** A player whose renders wait until the spec answers them. */
function setup() {
  const renders: {
    request: TryPronunciationRequest;
    signal: AbortSignal;
    answer: (b: Blob) => void;
    fail: (e: unknown) => void;
  }[] = [];
  const audios: FakeAudio[] = [];
  const revoked: string[] = [];
  const failures: ApiError[] = [];
  let n = 0;
  const player = new TryOutPlayer({
    render: (req, signal) =>
      new Promise<Blob>((answer, fail) => renders.push({ request: req, signal, answer, fail })),
    createAudio: (url) => {
      const audio = new FakeAudio(url);
      audios.push(audio);
      return audio;
    },
    createUrl: () => `blob:${++n}`,
    revokeUrl: (url) => revoked.push(url),
    failed: (error) => failures.push(error),
  });
  return { player, renders, audios, revoked, failures };
}

describe('TryOutPlayer', () => {
  it('shows generating while the host renders, then plays, then is idle at the end', async () => {
    const { player, renders, audios, revoked } = setup();
    player.toggle('card:spoken', [request('i1')]);
    expect(player.playing()).toEqual({
      key: 'card:spoken',
      phase: 'generating',
    });

    renders[0]!.answer(new Blob(['RIFF']));
    await flush();
    expect(player.playing()).toEqual({ key: 'card:spoken', phase: 'playing' });
    expect(audios[0]!.played).toBe(true);

    audios[0]!.end();
    await flush();
    expect(player.playing()).toBeNull();
    expect(revoked).toEqual(['blob:1']);
  });

  it('a second click stops: a rendering request is dropped, a sounding one paused', async () => {
    const { player, renders, audios } = setup();
    player.toggle('a', [request('i1')]);
    player.toggle('a', [request('i1')]);
    expect(renders[0]!.signal.aborted).toBe(true);
    expect(player.playing()).toBeNull();

    player.toggle('a', [request('i1')]);
    renders[1]!.answer(new Blob(['RIFF']));
    await flush();
    player.toggle('a', [request('i1')]);
    expect(audios[0]!.paused).toBe(true);
    expect(player.playing()).toBeNull();
  });

  it('plays one sample at a time: another play stops the first', async () => {
    const { player, renders, audios } = setup();
    player.toggle('a', [request('i1')]);
    renders[0]!.answer(new Blob(['RIFF']));
    await flush();
    player.toggle('b', [request('i2')]);
    expect(audios[0]!.paused).toBe(true);
    expect(player.playing()).toEqual({ key: 'b', phase: 'generating' });
  });

  it('plays a key’s requests in turn (Play all)', async () => {
    const { player, renders, audios } = setup();
    player.toggle('all', [request('i1'), request('i2')]);
    renders[0]!.answer(new Blob(['RIFF']));
    await flush();
    audios[0]!.end();
    await flush();
    expect(player.playing()).toEqual({ key: 'all', phase: 'generating' });
    expect(renders[1]!.request.itemId).toBe('i2');
    renders[1]!.answer(new Blob(['RIFF']));
    await flush();
    audios[1]!.end();
    await flush();
    expect(player.playing()).toBeNull();
  });

  it('hands a failed render to its caller; a stop is not a failure', async () => {
    const { player, renders, failures } = setup();
    player.toggle('a', [request('i1')]);
    renders[0]!.fail(
      new ApiError(409, 'Conflict', 'Can’t play while Attribution is running: it has the GPU.'),
    );
    await flush();
    expect(failures.map((f) => f.detail)).toEqual([
      'Can’t play while Attribution is running: it has the GPU.',
    ]);
    expect(player.playing()).toBeNull();

    player.toggle('a', [request('i1')]);
    player.toggle('a', [request('i1')]);
    renders[1]!.fail(new DOMException('Aborted', 'AbortError'));
    await flush();
    expect(failures).toHaveLength(1);
  });
});
