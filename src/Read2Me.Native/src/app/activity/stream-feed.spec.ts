import { beforeEach, describe, expect, it } from 'bun:test';
import { Emitter } from '@app/core/emitter';
import { override, resetServices, use } from '@app/core/services';
import type {
  AudioGenMessage,
  LiveSnapshot,
  LlmMessage,
  StreamKind,
} from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { AudioStreamFeed, LlmStreamFeed } from './stream-feed';

class FakeLive {
  readonly llm = new Emitter<LlmMessage>();
  readonly audioGen = new Emitter<AudioGenMessage>();
  readonly resyncedSubject = new Emitter<LiveSnapshot>();
  readonly joined: StreamKind[] = [];
  readonly left: StreamKind[] = [];

  on(family: string, listener: (m: unknown) => void) {
    if (family === 'llm') return this.llm.subscribe(listener as (m: LlmMessage) => void);
    if (family === 'audioGen')
      return this.audioGen.subscribe(listener as (m: AudioGenMessage) => void);
    throw new Error(`unexpected family ${family}`);
  }
  resynced(listener: (s: LiveSnapshot) => void) {
    return this.resyncedSubject.subscribe(listener);
  }
  async joinStream(kind: StreamKind) {
    this.joined.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.left.push(kind);
  }
}

let live: FakeLive;

beforeEach(() => {
  resetServices();
  live = new FakeLive();
  override(LiveService, live as unknown as LiveService);
});

const started: LlmMessage = {
  kind: 'requestStarted',
  paragraphPreview: 'p',
  prompt: 'q',
  configId: 1,
  configName: 'c',
};

describe('LlmStreamFeed', () => {
  it('joins stream:llm on the first acquire and leaves on the last release', () => {
    const llm = use(LlmStreamFeed);
    expect(live.joined).toEqual([]);

    llm.acquire();
    llm.acquire();
    expect(live.joined).toEqual(['llm']);

    llm.release();
    expect(live.left).toEqual([]);
    llm.release();
    expect(live.left).toEqual(['llm']);

    llm.release(); // over-release is harmless
    expect(live.left).toEqual(['llm']);
  });

  it('buffers mapped events only while acquired and clears them on re-acquire', () => {
    const llm = use(LlmStreamFeed);
    live.llm.emit(started);
    expect(llm.events()).toEqual([]);

    llm.acquire();
    live.llm.emit(started);
    live.llm.emit({ kind: 'delta', thinking: 't', content: 'c' });
    expect(llm.events().map((e) => e.kind)).toEqual([
      'requestStarted',
      'thinkingDelta',
      'contentDelta',
    ]);

    llm.release();
    live.llm.emit({ kind: 'delta', content: 'ignored' });
    expect(llm.events()).toHaveLength(3);

    llm.acquire();
    expect(llm.events()).toEqual([]);
  });

  it('caps the buffer at maxTurns turns', () => {
    const llm = use(LlmStreamFeed);
    llm.acquire();
    for (let i = 0; i < llm.maxUnits + 5; i++)
      live.llm.emit({ ...started, paragraphPreview: `p${i}` });
    const previews = llm.events().map((e) => (e as { paragraphPreview: string }).paragraphPreview);
    expect(previews).toHaveLength(llm.maxUnits);
    expect(previews[0]).toBe('p5');
  });

  it('clears the buffer on resync because the server replays the current turn', () => {
    const llm = use(LlmStreamFeed);
    llm.acquire();
    live.llm.emit(started);
    live.resyncedSubject.emit({} as LiveSnapshot);
    expect(llm.events()).toEqual([]);
  });
});

describe('AudioStreamFeed', () => {
  it('joins stream:audio and maps audio phases', () => {
    const audio = use(AudioStreamFeed);
    audio.acquire();
    expect(live.joined).toEqual(['audio']);
    live.audioGen.emit({ kind: 'itemStarted', id: 'i1', attempt: 1, character: 'Alice' });
    live.audioGen.emit({ kind: 'audioGenerated', id: 'i1', attempt: 1 });
    expect(audio.events().map((e) => e.kind)).toEqual(['itemStarted', 'audioGenerated']);
    audio.release();
    expect(live.left).toEqual(['audio']);
  });
});
