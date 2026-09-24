import { Injectable, inject, signal } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { AudioGenEvent, LlmStreamEvent } from '@app/live/hub-events';
import { AudioGenMessage, LlmMessage, StreamKind } from '@app/live/live-messages';
import { LiveService } from '@app/live/live.service';
import {
  appendCapped,
  mapAudioGenMessage,
  mapLlmMessage,
  startsAudioCard,
  startsLlmTurn,
} from './stream-events';

/** Turns / cards a stream tab keeps (ticket 14). */
export const STREAM_MAX_UNITS = 50;

/**
 * A reference-counted event buffer behind one stream tab (design §9: the stream group is joined
 * only while the tab is visible). The first `acquire` clears the buffer, subscribes to the family
 * and joins the hub group — in that order, so the server's replay of the in-progress turn lands in
 * the buffer; the last `release` leaves the group. A resync clears the buffer because the server
 * replays the current turn again on re-join.
 */
export abstract class StreamFeed<TWire, TEvent> {
  private readonly _events = signal<TEvent[]>([]);
  private count = 0;
  private subscription: Subscription | null = null;

  readonly events = this._events.asReadonly();

  protected constructor(
    private readonly live: LiveService,
    private readonly kind: StreamKind,
    private readonly source: (live: LiveService) => Observable<TWire>,
    private readonly map: (m: TWire) => readonly TEvent[],
    private readonly startsUnit: (e: TEvent) => boolean,
    readonly maxUnits: number,
  ) {}

  acquire(): void {
    if (this.count++ > 0) return;
    this._events.set([]);
    this.subscription = new Subscription();
    this.subscription.add(this.source(this.live).subscribe((m) => this.receive(m)));
    this.subscription.add(this.live.resynced$.subscribe(() => this._events.set([])));
    void this.live.joinStream(this.kind);
  }

  release(): void {
    if (this.count === 0) return;
    if (--this.count > 0) return;
    this.subscription?.unsubscribe();
    this.subscription = null;
    void this.live.leaveStream(this.kind);
  }

  private receive(m: TWire): void {
    const mapped = this.map(m);
    if (mapped.length === 0) return;
    this._events.update((events) => appendCapped(events, mapped, this.startsUnit, this.maxUnits));
  }
}

@Injectable({ providedIn: 'root' })
export class LlmStreamFeed extends StreamFeed<LlmMessage, LlmStreamEvent> {
  constructor() {
    super(
      inject(LiveService),
      'llm',
      (live) => live.on('llm'),
      mapLlmMessage,
      startsLlmTurn,
      STREAM_MAX_UNITS,
    );
  }
}

@Injectable({ providedIn: 'root' })
export class AudioStreamFeed extends StreamFeed<AudioGenMessage, AudioGenEvent> {
  constructor() {
    super(
      inject(LiveService),
      'audio',
      (live) => live.on('audioGen'),
      (m) => [mapAudioGenMessage(m)],
      startsAudioCard,
      STREAM_MAX_UNITS,
    );
  }
}
