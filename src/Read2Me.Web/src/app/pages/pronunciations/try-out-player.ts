import { type ApiError, type TryPronunciationRequest, toApiError } from '@app/api';
import { signal } from '@app/core/signals';

/** What is playing: the button's key, and whether the host is still rendering or it is sounding. */
export interface TryOutPlaying {
  key: string;
  phase: 'generating' | 'playing';
}

/** The little of `HTMLAudioElement` the player uses, so specs can stand one in. */
export interface PlayableAudio {
  play(): Promise<void>;
  pause(): void;
  addEventListener(type: 'ended' | 'error', listener: () => void): void;
}

export interface TryOutPlayerDeps {
  render: (request: TryPronunciationRequest, signal: AbortSignal) => Promise<Blob>;
  /** A play that failed, with the host's refusal; a stop is not a failure. */
  failed: (error: ApiError) => void;
  createAudio?: (url: string) => PlayableAudio;
  createUrl?: (blob: Blob) => string;
  revokeUrl?: (url: string) => void;
}

/**
 * Try it's one player (ticket 13): one sample at a time. A play shows "Generating…" while the host
 * renders, then sounds; a second click on the playing button stops it, dropping the request if it
 * is still rendering; any other play stops the first. A key plays its requests in turn (Play all).
 */
export class TryOutPlayer {
  readonly playing = signal<TryOutPlaying | null>(null);

  private readonly createAudio: (url: string) => PlayableAudio;
  private readonly createUrl: (blob: Blob) => string;
  private readonly revokeUrl: (url: string) => void;
  private abort: AbortController | null = null;
  private audio: PlayableAudio | null = null;
  private finish: (() => void) | null = null;

  constructor(private readonly deps: TryOutPlayerDeps) {
    this.createAudio = deps.createAudio ?? ((url) => new Audio(url));
    this.createUrl = deps.createUrl ?? ((blob) => URL.createObjectURL(blob));
    this.revokeUrl = deps.revokeUrl ?? ((url) => URL.revokeObjectURL(url));
  }

  /** Plays `requests` under `key`, or stops when `key` is what is playing. */
  toggle(key: string, requests: readonly TryPronunciationRequest[]): void {
    if (this.playing()?.key === key) {
      this.stop();
      return;
    }
    void this.run(key, requests);
  }

  /** Stops whatever plays, dropping a request still rendering. */
  stop(): void {
    this.abort?.abort();
    this.abort = null;
    this.audio?.pause();
    this.audio = null;
    this.finish?.();
    this.finish = null;
    this.playing.set(null);
  }

  private async run(key: string, requests: readonly TryPronunciationRequest[]): Promise<void> {
    this.stop();
    const abort = new AbortController();
    this.abort = abort;
    try {
      for (const request of requests) {
        this.playing.set({ key, phase: 'generating' });
        const blob = await this.deps.render(request, abort.signal);
        if (abort.signal.aborted) return;
        await this.sound(key, blob, abort);
        if (abort.signal.aborted) return;
      }
    } catch (e) {
      if (abort.signal.aborted) return;
      this.deps.failed(toApiError(e));
    }
    if (this.abort === abort) {
      this.abort = null;
      this.playing.set(null);
    }
  }

  /** Sounds one rendering to its end (or until stopped). */
  private async sound(key: string, blob: Blob, abort: AbortController): Promise<void> {
    const url = this.createUrl(blob);
    try {
      const audio = this.createAudio(url);
      this.audio = audio;
      this.playing.set({ key, phase: 'playing' });
      await new Promise<void>((resolve, reject) => {
        this.finish = resolve;
        audio.addEventListener('ended', resolve);
        audio.addEventListener('error', () => reject(new Error("Can't play the sample")));
        audio.play().catch(reject);
      });
    } finally {
      this.revokeUrl(url);
      if (!abort.signal.aborted) {
        this.audio = null;
        this.finish = null;
      }
    }
  }
}
