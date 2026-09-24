import { ChangeDetectionStrategy, Component, OnDestroy, inject } from '@angular/core';
import { StreamAudio } from '@app/ui/stream-audio/stream-audio';
import { StreamLlm } from '@app/ui/stream-llm/stream-llm';
import { AudioStreamFeed, LlmStreamFeed } from './stream-feed';

/**
 * The drawer's LLM tab (ticket 14, design §9): exists only while the tab is visible, and its
 * lifetime is the feed's reference — construct joins `stream:llm`, destroy leaves it.
 */
@Component({
  selector: 'app-llm-stream-tab',
  imports: [StreamLlm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-stream-tab' },
  template: `<r2m-stream-llm [events]="feed.events()" [maxTurns]="feed.maxUnits" />`,
  styles: `
    :host {
      display: block;
      height: 100%;
      min-height: 0;
    }
  `,
})
export class LlmStreamTab implements OnDestroy {
  protected readonly feed = inject(LlmStreamFeed);

  constructor() {
    this.feed.acquire();
  }

  ngOnDestroy(): void {
    this.feed.release();
  }
}

/** The drawer's Audio tab: same join-while-visible rule over `stream:audio`. */
@Component({
  selector: 'app-audio-stream-tab',
  imports: [StreamAudio],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'activity-stream-tab' },
  template: `<r2m-stream-audio [events]="feed.events()" [maxCards]="feed.maxUnits" />`,
  styles: `
    :host {
      display: block;
      height: 100%;
      min-height: 0;
    }
  `,
})
export class AudioStreamTab implements OnDestroy {
  protected readonly feed = inject(AudioStreamFeed);

  constructor() {
    this.feed.acquire();
  }

  ngOnDestroy(): void {
    this.feed.release();
  }
}
