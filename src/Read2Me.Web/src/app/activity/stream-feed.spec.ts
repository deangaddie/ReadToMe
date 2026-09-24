import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AudioGenMessage, LiveSnapshot, LlmMessage, StreamKind } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import { AudioStreamFeed, LlmStreamFeed } from './stream-feed';

class FakeLive {
  readonly llm = new Subject<LlmMessage>();
  readonly audioGen = new Subject<AudioGenMessage>();
  readonly resynced = new Subject<LiveSnapshot>();
  readonly resynced$ = this.resynced.asObservable();
  readonly joined: StreamKind[] = [];
  readonly left: StreamKind[] = [];

  on(family: string) {
    if (family === 'llm') return this.llm.asObservable();
    if (family === 'audioGen') return this.audioGen.asObservable();
    throw new Error(`unexpected family ${family}`);
  }
  async joinStream(kind: StreamKind) {
    this.joined.push(kind);
  }
  async leaveStream(kind: StreamKind) {
    this.left.push(kind);
  }
}

function setup() {
  const live = new FakeLive();
  TestBed.configureTestingModule({ providers: [{ provide: LiveService, useValue: live }] });
  return { live, llm: TestBed.inject(LlmStreamFeed), audio: TestBed.inject(AudioStreamFeed) };
}

const started: LlmMessage = {
  kind: 'requestStarted',
  paragraphPreview: 'p',
  prompt: 'q',
  configId: 1,
  configName: 'c',
};

describe('LlmStreamFeed', () => {
  it('joins stream:llm on the first acquire and leaves on the last release', () => {
    const { live, llm } = setup();
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
    const { live, llm } = setup();
    live.llm.next(started);
    expect(llm.events()).toEqual([]);

    llm.acquire();
    live.llm.next(started);
    live.llm.next({ kind: 'delta', thinking: 't', content: 'c' });
    expect(llm.events().map((e) => e.kind)).toEqual([
      'requestStarted',
      'thinkingDelta',
      'contentDelta',
    ]);

    llm.release();
    live.llm.next({ kind: 'delta', content: 'ignored' });
    expect(llm.events()).toHaveLength(3);

    llm.acquire();
    expect(llm.events()).toEqual([]);
  });

  it('caps the buffer at maxTurns turns', () => {
    const { live, llm } = setup();
    llm.acquire();
    for (let i = 0; i < llm.maxUnits + 5; i++)
      live.llm.next({ ...started, paragraphPreview: `p${i}` });
    const previews = llm.events().map((e) => (e as { paragraphPreview: string }).paragraphPreview);
    expect(previews).toHaveLength(llm.maxUnits);
    expect(previews[0]).toBe('p5');
  });

  it('clears the buffer on resync because the server replays the current turn', () => {
    const { live, llm } = setup();
    llm.acquire();
    live.llm.next(started);
    live.resynced.next({} as LiveSnapshot);
    expect(llm.events()).toEqual([]);
  });
});

describe('AudioStreamFeed', () => {
  it('joins stream:audio and maps audio phases', () => {
    const { live, audio } = setup();
    audio.acquire();
    expect(live.joined).toEqual(['audio']);
    live.audioGen.next({ kind: 'itemStarted', id: 'i1', attempt: 1, character: 'Alice' });
    live.audioGen.next({ kind: 'audioGenerated', id: 'i1', attempt: 1 });
    expect(audio.events().map((e) => e.kind)).toEqual(['itemStarted', 'audioGenerated']);
    audio.release();
    expect(live.left).toEqual(['audio']);
  });
});
