import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ParagraphDto } from '@app/api';
import { speakerHue } from '@app/shared/speaker-color';
import { SpeakerChip } from '@app/ui/speaker-chip/speaker-chip';
import { StatusChip } from '@app/ui/status-chip/status-chip';
import { ItemRow } from './item-row';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';
import {
  RowContext,
  clearableOutcome,
  isBusy,
  paragraphSpeaker,
  paragraphText,
  queueChip,
} from './reader-rows';
import { ancestryFor, isDialogParagraph } from './selection';
import { SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';

/**
 * One paragraph in the reader (design §6.3): a gutter with the selection checkbox (Character
 * paragraphs only, in Read and Speakers modes — ticket 12) and a rail in the speaker's colour,
 * then the body the mode chooses — prose with one paragraph speaker chip in Read, one
 * {@link ItemRow} per item in Speakers and Audio. The Read chip opens the speaker menu and assigns
 * the whole paragraph. The paragraph menu (ticket 11) sits at the right in every mode, off while
 * attribution is queued; a settled Failed/Unknown chip is a button that forgets the outcome.
 */
@Component({
  selector: 'r2m-paragraph',
  imports: [MatIconModule, MatTooltipModule, ItemRow, NodeMenu, SpeakerChip, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-paragraph',
    '[class]': '"r2m-paragraph--" + speaker().state',
    '[class.r2m-paragraph--selected]': 'selected()',
    '[style.--r2m-speaker-hue]': 'hue()',
    '[attr.data-paragraph-id]': 'paragraph().id',
  },
  template: `
    <div class="r2m-paragraph__gutter">
      @if (selectable()) {
        <input
          type="checkbox"
          class="r2m-paragraph__select"
          aria-label="Select paragraph"
          [checked]="selected()"
          (change)="toggle($event)"
        />
      } @else {
        <span class="r2m-paragraph__select r2m-paragraph__select--none" aria-hidden="true"></span>
      }
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
            [interactive]="assignable()"
            [roster]="assignable() ? ctx().roster : undefined"
            (pick)="assign($event)"
            (clear)="assign(null)"
            (create)="create($event)"
          />
        </div>
        <p class="r2m-paragraph__prose">{{ text() }}</p>
      } @else {
        @for (item of paragraph().items; track item.id) {
          <r2m-item
            [item]="item"
            [paragraphId]="paragraph().id"
            [chapterId]="chapterId()"
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
          @if (outcome()) {
            <button
              type="button"
              class="r2m-paragraph__outcome"
              [matTooltip]="(chip.tooltip ? chip.tooltip + ' — ' : '') + 'Click to clear'"
              (click)="clearOutcome()"
            >
              <r2m-status-chip compact [status]="chip.status" [label]="chip.label" [icon]="chip.icon" />
            </button>
          } @else {
            <r2m-status-chip
              compact
              [status]="chip.status"
              [label]="chip.label"
              [icon]="chip.icon"
              [tooltip]="chip.tooltip ?? ''"
            />
          }
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
      border-radius: var(--r2m-radius-sm);
      @include tokens.speaker-colors;
      --_rail: var(--r2m-speaker-fg);
    }
    :host(.r2m-paragraph--selected) {
      background: color-mix(in srgb, var(--r2m-accent) 8%, transparent);
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
      width: 13px;
      height: 13px;
      accent-color: var(--r2m-accent);
    }
    .r2m-paragraph__select--none {
      display: inline-block;
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
    .r2m-paragraph__outcome {
      padding: 0;
      border: none;
      background: transparent;
      font: inherit;
      cursor: pointer;
    }
    .r2m-paragraph__outcome:focus-visible {
      outline: 2px solid var(--r2m-accent);
      outline-offset: 1px;
      border-radius: var(--r2m-radius-pill);
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
  private readonly selection = inject(SelectionStore);
  private readonly assigner = inject(SpeakerAssigner);

  readonly paragraph = input.required<ParagraphDto>();
  readonly ctx = input.required<RowContext>();
  /** The chapter the paragraph is in (the row knows; the DTO does not). */
  readonly chapterId = input<string>('');
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
  protected readonly outcome = computed(() => clearableOutcome(this.status()));
  protected readonly busy = computed(() => isBusy(this.status()));

  /** Only Character paragraphs are selectable, and only where the selection is paragraphs. */
  protected readonly selectable = computed(
    () => this.ctx().selectable && isDialogParagraph(this.paragraph()),
  );
  protected readonly selected = computed(() => this.ctx().selected.has(this.paragraph().id));
  /** The Read chip assigns the whole paragraph; a queued one is the server's to stamp. */
  protected readonly assignable = computed(
    () => this.selectable() && !this.busy() && this.ctx().roster.length > 0,
  );

  protected toggle(event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    this.selection.toggle(this.paragraph().id, ancestryFor(this.ctx().ancestry, this.chapterId()), on);
  }

  protected assign(characterId: string | null): void {
    void this.assigner.assign({ kind: 'paragraph', paragraphId: this.paragraph().id }, characterId);
  }

  protected create(name: string): void {
    void this.assigner.createAndAssign({ kind: 'paragraph', paragraphId: this.paragraph().id }, name);
  }

  protected clearOutcome(): void {
    void this.assigner.clearOutcome(this.paragraph().id);
  }
}
