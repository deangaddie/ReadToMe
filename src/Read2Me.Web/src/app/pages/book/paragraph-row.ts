import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { ParagraphDto } from '@app/api';
import { speakerHue } from '@app/shared/speaker-color';
import { SpeakerChip } from '@app/ui/speaker-chip/speaker-chip';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ItemRow } from './item-row';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';
import { RowContext, isBusy, paragraphSpeaker, paragraphText, queueChip } from './reader-rows';

/**
 * One paragraph in the reader (design §6.3): a gutter with the selection checkbox (inert until
 * tickets 12/13) and a rail in the speaker's colour, then the body the mode chooses — prose with one
 * paragraph speaker chip in Read, one {@link ItemRow} per item in Speakers and Audio. The
 * paragraph menu (ticket 11) sits at the right in every mode, off while attribution is queued.
 */
@Component({
  selector: 'r2m-paragraph',
  imports: [MatIconModule, ItemRow, NodeMenu, SpeakerChip, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-paragraph',
    '[class]': '"r2m-paragraph--" + speaker().state',
    '[style.--r2m-speaker-hue]': 'hue()',
    '[attr.data-paragraph-id]': 'paragraph().id',
  },
  template: `
    <div class="r2m-paragraph__gutter">
      <input type="checkbox" class="r2m-paragraph__select" disabled aria-label="Select paragraph" />
      <span class="r2m-paragraph__rail" aria-hidden="true"></span>
    </div>

    <div class="r2m-paragraph__body">
      @if (ctx().mode === 'read') {
        <div class="r2m-paragraph__meta">
          <r2m-speaker-chip
            compact
            [state]="speaker().state"
            [name]="speaker().name"
            [characterId]="speaker().characterId"
          />
        </div>
        <p class="r2m-paragraph__prose">{{ text() }}</p>
      } @else {
        @for (item of paragraph().items; track item.id) {
          <r2m-item
            [item]="item"
            [ctx]="ctx()"
            [isFirst]="$first"
            [isLast]="$last"
            [paragraphBusy]="busy()"
          />
        }
      }
    </div>

    <div class="r2m-paragraph__status">
      @if (ctx().mode !== 'audio') {
        @if (queue(); as chip) {
          <r2m-status-chip
            compact
            [status]="chip.status"
            [label]="chip.label"
            [icon]="chip.icon"
            [tooltip]="chip.tooltip ?? ''"
          />
        }
        @if (busy()) {
          <mat-icon class="r2m-paragraph__lock" aria-label="Locked while queued">lock</mat-icon>
        }
      }
      <r2m-node-menu class="r2m-paragraph__menu" [target]="menuTarget()" [disabled]="busy()" />
    </div>
  `,
  styles: `
    @use 'tokens';
    :host {
      display: flex;
      align-items: stretch;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-1) 0;
      @include tokens.speaker-colors;
      --_rail: var(--r2m-speaker-fg);
    }
    :host(.r2m-paragraph--unknown) {
      --_rail: var(--r2m-status-warn);
    }
    :host(.r2m-paragraph--mixed) {
      --_rail: var(--r2m-status-neutral);
    }
    :host(.r2m-paragraph--narration) {
      --_rail: var(--r2m-outline);
    }
    .r2m-paragraph__gutter {
      flex: 0 0 auto;
      display: flex;
      align-items: flex-start;
      gap: var(--r2m-space-2);
      padding-top: 4px;
    }
    .r2m-paragraph__select {
      margin: 2px 0 0;
    }
    .r2m-paragraph__rail {
      width: 3px;
      align-self: stretch;
      border-radius: 2px;
      background: var(--_rail);
    }
    .r2m-paragraph__body {
      flex: 1 1 auto;
      min-width: 0;
    }
    .r2m-paragraph__meta {
      margin-bottom: 2px;
    }
    .r2m-paragraph__prose {
      margin: 0;
      line-height: var(--r2m-line-normal);
      font-size: var(--r2m-text-md);
    }
    .r2m-paragraph__status {
      flex: 0 0 auto;
      display: flex;
      align-items: flex-start;
      gap: var(--r2m-space-1);
      padding-top: 2px;
    }
    .r2m-paragraph__lock {
      font-size: 16px;
      width: 16px;
      height: 16px;
      color: var(--r2m-text-muted);
    }
    .r2m-paragraph__menu {
      opacity: 0;
      transition: opacity 120ms;
    }
    :host(:hover) .r2m-paragraph__menu,
    :host(:focus-within) .r2m-paragraph__menu {
      opacity: 1;
    }
  `,
})
export class ParagraphRow {
  readonly paragraph = input.required<ParagraphDto>();
  readonly ctx = input.required<RowContext>();
  /** Position among the chapter's paragraphs (merge entries hide at the ends). */
  readonly isFirst = input(false);
  readonly isLast = input(false);

  protected readonly menuTarget = computed<NodeMenuTarget>(() => ({
    kind: 'paragraph',
    id: this.paragraph().id,
    text: paragraphText(this.paragraph()),
    isFirst: this.isFirst(),
    isLast: this.isLast(),
  }));

  protected readonly speaker = computed(() =>
    paragraphSpeaker(this.paragraph(), this.ctx().speakers),
  );
  protected readonly hue = computed(() =>
    speakerHue(this.speaker().characterId ?? this.speaker().name),
  );
  protected readonly text = computed(() => paragraphText(this.paragraph()));
  private readonly status = computed(() => this.ctx().paragraphStatus[this.paragraph().id]);
  protected readonly queue = computed(() => queueChip(this.status(), 'Unknown'));
  protected readonly busy = computed(() => isBusy(this.status()));
}
