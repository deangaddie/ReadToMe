import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { ParagraphItemDto, workspaceUrl } from '@app/api';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { SpeakerChip } from '@app/ui/speaker-chip/speaker-chip';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';
import {
  RowContext,
  isBusy,
  itemSpeaker,
  pauseLabel,
  queueChip,
  reviewChip,
  reviewOf,
  voiceLine,
} from './reader-rows';

/**
 * One paragraph item in Speakers or Audio mode (design §6.3). Speakers: speaker chip and text, an
 * unknown speaker highlighted. Audio: adds the resolved voice, voice instructions, review and
 * queue chips and a player for the generated take (`audioVersion` busts the browser cache).
 * Every item carries its menu (ticket 11), off while the item or its paragraph is queued.
 */
@Component({
  selector: 'r2m-item',
  imports: [MatIconModule, AudioPlayer, SpeakerChip, StatusChip, NodeMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-item',
    '[class.r2m-item--unknown]': 'unknown()',
    '[class.r2m-item--pause]': 'item().isPause',
    '[attr.data-item-id]': 'item().id',
  },
  template: `
    @if (item().isPause) {
      <span class="r2m-item__pause">{{ pause() }}</span>
    } @else {
      <div class="r2m-item__line">
        <r2m-speaker-chip
          compact
          class="r2m-item__speaker"
          [state]="speaker().state"
          [name]="speaker().name"
          [characterId]="speaker().characterId"
        />
        <span class="r2m-item__text">{{ item().text }}</span>
        @if (busy()) {
          <mat-icon class="r2m-item__lock" aria-label="Locked while queued">lock</mat-icon>
        }
        <r2m-node-menu
          class="r2m-item__menu"
          [target]="menuTarget()"
          [disabled]="busy() || paragraphBusy()"
        />
      </div>

      @if (ctx().mode === 'audio') {
        <div class="r2m-item__audio">
          <span class="r2m-item__voice" data-testid="voice-line">{{ voice() }}</span>
          @if (item().voiceInstructions) {
            <span class="r2m-item__instructions">{{ item().voiceInstructions }}</span>
          }
          <span class="r2m-item__chips">
            @if (review(); as chip) {
              <r2m-status-chip
                compact
                [status]="chip.status"
                [label]="chip.label"
                [icon]="chip.icon"
                [tooltip]="chip.tooltip ?? ''"
              />
            }
            @if (queue(); as chip) {
              <r2m-status-chip
                compact
                [status]="chip.status"
                [label]="chip.label"
                [icon]="chip.icon"
                [tooltip]="chip.tooltip ?? ''"
              />
            }
          </span>
          @if (audioSrc(); as src) {
            <r2m-audio-player compact [src]="src" [cacheKey]="audioVersion()" />
          }
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: block;
      padding: 2px var(--r2m-space-2);
      border-radius: var(--r2m-radius-sm);
    }
    :host(.r2m-item--unknown) {
      background: var(--r2m-status-warn-soft);
    }
    .r2m-item__line {
      display: flex;
      align-items: baseline;
      gap: var(--r2m-space-2);
    }
    .r2m-item__speaker {
      flex: 0 0 auto;
    }
    .r2m-item__text {
      flex: 1 1 auto;
      min-width: 0;
      line-height: var(--r2m-line-normal);
    }
    .r2m-item__lock {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--r2m-text-muted);
    }
    .r2m-item__menu {
      align-self: center;
      opacity: 0;
      transition: opacity 120ms;
    }
    :host(:hover) .r2m-item__menu,
    :host(:focus-within) .r2m-item__menu {
      opacity: 1;
    }
    .r2m-item__audio {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--r2m-space-1) var(--r2m-space-3);
      margin: 2px 0 var(--r2m-space-1);
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .r2m-item__instructions {
      font-style: italic;
    }
    .r2m-item__chips {
      display: inline-flex;
      gap: var(--r2m-space-1);
    }
    .r2m-item__pause {
      font-size: var(--r2m-text-sm);
      font-style: italic;
      color: var(--r2m-text-muted);
    }
  `,
})
export class ItemRow {
  readonly item = input.required<ParagraphItemDto>();
  readonly ctx = input.required<RowContext>();
  /** Position among the paragraph's items (merge entries hide at the ends). */
  readonly isFirst = input(false);
  readonly isLast = input(false);
  /** The paragraph's attribution is queued or running: the server would refuse an item edit. */
  readonly paragraphBusy = input(false);

  protected readonly menuTarget = computed<NodeMenuTarget>(() => ({
    kind: 'item',
    id: this.item().id,
    text: this.item().text,
    isFirst: this.isFirst(),
    isLast: this.isLast(),
    isPause: this.item().isPause,
  }));

  protected readonly speaker = computed(() => itemSpeaker(this.item(), this.ctx().speakers));
  protected readonly unknown = computed(
    () => !this.item().isPause && this.speaker().state === 'unknown',
  );
  protected readonly pause = computed(() => pauseLabel(this.item()));
  private readonly status = computed(() => this.ctx().itemStatus[this.item().id]);
  /** Item queue state is audio's; attribution locks the whole paragraph instead. */
  protected readonly busy = computed(() => this.ctx().mode === 'audio' && isBusy(this.status()));
  protected readonly voice = computed(() => voiceLine(this.ctx().voices[this.item().id]));
  protected readonly review = computed(() => reviewChip(reviewOf(this.ctx(), this.item().id)));
  protected readonly queue = computed(() => queueChip(this.status()));
  protected readonly audioVersion = computed(() => this.status()?.audioVersion ?? undefined);
  protected readonly audioSrc = computed(() => {
    const file = this.item().audioFileName;
    return file ? workspaceUrl(this.ctx().folder, file) : null;
  });
}
