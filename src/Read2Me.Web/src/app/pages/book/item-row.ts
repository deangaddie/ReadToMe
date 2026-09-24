import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ParagraphItemDto, workspaceUrl } from '@app/api';
import { AudioPlayer } from '@app/ui/audio-player/audio-player';
import { SpeakerChip } from '@app/ui/speaker-chip/speaker-chip';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { AudioGenerator } from './audio-generator';
import { AudioSelectionStore } from './audio-selection-store';
import { BookEditor } from './book-editor';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';
import {
  RowContext,
  clearableOutcome,
  isBusy,
  itemSpeaker,
  pauseLabel,
  queueChip,
  reviewChip,
  reviewOf,
  voiceLine,
} from './reader-rows';
import { ancestryFor, isVoicedItem } from './selection';
import { SpeakerAssigner } from './speaker-assigner';

/**
 * One paragraph item in Speakers or Audio mode (design §6.3). Speakers: speaker chip and text, an
 * unknown speaker highlighted; the chip opens the speaker menu and assigns this item (ticket 12).
 * Audio (ticket 13): a selection checkbox (off for a line nobody can read yet), the resolved
 * voice, voice instructions, the review chip with its dismiss (a dismissed review is a faded
 * icon), the queue chip with a Retry on a settled failure, and a player for the generated take
 * (`audioVersion` busts the browser cache). Every item carries its menu (ticket 11), off while the
 * item or its paragraph is queued.
 */
@Component({
  selector: 'r2m-item',
  imports: [MatIconModule, MatTooltipModule, AudioPlayer, SpeakerChip, StatusChip, NodeMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-item',
    '[class.r2m-item--unknown]': 'unknown()',
    '[class.r2m-item--pause]': 'item().isPause',
    '[class.r2m-item--selected]': 'selected()',
    '[attr.data-item-id]': 'item().id',
  },
  template: `
    @if (item().isPause) {
      <span class="r2m-item__pause">{{ pause() }}</span>
    } @else {
      <div class="r2m-item__line">
        @if (ctx().itemSelectable) {
          <input
            type="checkbox"
            class="r2m-item__select"
            aria-label="Select item"
            [checked]="selected()"
            [disabled]="!voiced()"
            [matTooltip]="voiced() ? '' : 'Assign a speaker before generating audio'"
            (change)="toggle($event)"
          />
        }
        <r2m-speaker-chip
          compact
          class="r2m-item__speaker"
          [state]="speaker().state"
          [name]="speaker().name"
          [characterId]="speaker().characterId"
          [interactive]="assignable()"
          [roster]="assignable() ? ctx().roster : undefined"
          (pick)="assign($event)"
          (clear)="assign(null)"
          (create)="create($event)"
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
              @if (reviewDismissed()) {
                <mat-icon
                  class="r2m-item__review-dismissed"
                  data-testid="review-dismissed"
                  aria-label="Review dismissed"
                  matTooltip="Review dismissed"
                  >{{ chip.icon }}</mat-icon
                >
              } @else {
                <r2m-status-chip
                  compact
                  [status]="chip.status"
                  [label]="chip.label"
                  [icon]="chip.icon"
                  [tooltip]="chip.tooltip ?? ''"
                />
                <button
                  type="button"
                  class="r2m-item__action"
                  data-action="dismiss-review"
                  [disabled]="editor.locked()"
                  (click)="dismissReview()"
                >
                  Dismiss
                </button>
              }
            }
            @if (queue(); as chip) {
              <r2m-status-chip
                compact
                [status]="chip.status"
                [label]="chip.label"
                [icon]="chip.icon"
                [tooltip]="chip.tooltip ?? ''"
              />
              @if (outcome() && voiced()) {
                <button
                  type="button"
                  class="r2m-item__action"
                  data-action="retry-audio"
                  [disabled]="generator.working() || editor.locked()"
                  (click)="retry()"
                >
                  Retry
                </button>
              }
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
    :host(.r2m-item--selected) {
      background: color-mix(in srgb, var(--r2m-accent) 8%, transparent);
    }
    .r2m-item__line {
      display: flex;
      align-items: baseline;
      gap: var(--r2m-space-2);
    }
    .r2m-item__select {
      flex: 0 0 auto;
      margin: 0;
      width: 13px;
      height: 13px;
      accent-color: var(--r2m-accent);
      align-self: center;
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
      align-items: center;
      gap: var(--r2m-space-1);
    }
    .r2m-item__action {
      padding: 0 var(--r2m-space-1);
      border: none;
      border-radius: var(--r2m-radius-sm);
      background: transparent;
      color: var(--r2m-accent);
      font: inherit;
      font-size: var(--r2m-text-xs);
      font-weight: 600;
      cursor: pointer;
    }
    .r2m-item__action:disabled {
      color: var(--r2m-text-muted);
      cursor: default;
    }
    .r2m-item__action:focus-visible {
      outline: 2px solid var(--r2m-accent);
    }
    .r2m-item__review-dismissed {
      font-size: 16px;
      width: 16px;
      height: 16px;
      opacity: 0.4;
    }
    .r2m-item__pause {
      font-size: var(--r2m-text-sm);
      font-style: italic;
      color: var(--r2m-text-muted);
    }
  `,
})
export class ItemRow {
  private readonly assigner = inject(SpeakerAssigner);
  private readonly selection = inject(AudioSelectionStore);
  protected readonly generator = inject(AudioGenerator);
  protected readonly editor = inject(BookEditor);

  readonly item = input.required<ParagraphItemDto>();
  /** The paragraph the item is in: an assign forgets that paragraph's attribution outcome. */
  readonly paragraphId = input<string>('');
  /** The chapter the item is in (the row knows; the DTO does not): a ticked item rolls up into it. */
  readonly chapterId = input<string>('');
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
  private readonly reviewInfo = computed(() => reviewOf(this.ctx(), this.item().id));
  protected readonly review = computed(() => reviewChip(this.reviewInfo()));
  protected readonly reviewDismissed = computed(() => this.reviewInfo()?.state === 'Dismissed');
  protected readonly queue = computed(() => queueChip(this.status()));
  /** A settled Failed/Unfinished take: Retry re-queues just this item (once somebody can read it). */
  protected readonly outcome = computed(() => clearableOutcome(this.status()));
  protected readonly audioVersion = computed(() => this.status()?.audioVersion ?? undefined);
  protected readonly audioSrc = computed(() => {
    const file = this.item().audioFileName;
    return file ? workspaceUrl(this.ctx().folder, file) : null;
  });

  /** Somebody can read the line, so it may be picked for generation (ticket 13). */
  protected readonly voiced = computed(() => isVoicedItem(this.item(), this.ctx().narratorOnlyMode));
  protected readonly selected = computed(
    () => this.ctx().itemSelectable && this.ctx().selectedItems.has(this.item().id),
  );

  /** Speakers mode only; a paragraph the queue holds is the server's to stamp. */
  protected readonly assignable = computed(
    () =>
      this.ctx().mode === 'speakers' &&
      !this.paragraphBusy() &&
      !this.busy() &&
      this.ctx().roster.length > 0,
  );

  protected toggle(event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    this.selection.toggle(this.item().id, ancestryFor(this.ctx().ancestry, this.chapterId()), on);
  }

  protected retry(): void {
    void this.generator.retry(this.item().id);
  }

  protected dismissReview(): void {
    void this.generator.dismissReview(this.item().id);
  }

  protected assign(characterId: string | null): void {
    void this.assigner.assign(
      { kind: 'item', itemId: this.item().id, paragraphId: this.paragraphId() },
      characterId,
    );
  }

  protected create(name: string): void {
    void this.assigner.createAndAssign(
      { kind: 'item', itemId: this.item().id, paragraphId: this.paragraphId() },
      name,
    );
  }
}
