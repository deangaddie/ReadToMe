import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CharacterLineDto } from '@app/api';
import { LineContext } from './line-context';

/** Every row is one line high, so the list can virtualise over thousands of lines. */
export const LINE_ROW_HEIGHT = 36;

/**
 * A character's lines (research §4 "Lines"): a virtual list of single-line rows. Clicking the
 * text opens the reader at that chapter; the chevron expands one line's context beneath the
 * list (one at a time, so the rows keep their fixed height).
 */
@Component({
  selector: 'app-character-lines',
  imports: [ScrollingModule, MatButtonModule, MatIconModule, MatTooltipModule, LineContext],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'character-lines' },
  template: `
    @if (lines().length === 0) {
      <p class="character-lines__empty">
        {{ loading() ? 'Loading lines…' : 'No lines attributed to this character.' }}
      </p>
    } @else {
      <cdk-virtual-scroll-viewport
        class="character-lines__viewport"
        [itemSize]="rowHeight"
        [style.height.px]="viewportHeight()"
      >
        <div
          *cdkVirtualFor="let line of lines(); trackBy: trackLine"
          class="character-lines__row"
          [class.character-lines__row--expanded]="expandedId() === line.itemId"
          [attr.data-item-id]="line.itemId"
        >
          <button
            mat-icon-button
            type="button"
            class="character-lines__toggle"
            [attr.aria-expanded]="expandedId() === line.itemId"
            [attr.aria-label]="expandedId() === line.itemId ? 'Hide context' : 'Show context'"
            (click)="toggle(line)"
          >
            <mat-icon>{{ expandedId() === line.itemId ? 'expand_less' : 'expand_more' }}</mat-icon>
          </button>
          <button
            type="button"
            class="character-lines__text"
            matTooltip="Open in the reader"
            matTooltipShowDelay="600"
            (click)="open.emit(line)"
          >
            {{ line.text }}
          </button>
        </div>
      </cdk-virtual-scroll-viewport>
      @if (expanded(); as line) {
        <div class="character-lines__context">
          <div class="character-lines__context-line">{{ line.text }}</div>
          <app-line-context [folder]="folder()" [line]="line" />
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .character-lines__empty {
      margin: 0;
      padding: var(--r2m-space-2);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .character-lines__viewport {
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
    }
    .character-lines__row {
      display: flex;
      align-items: center;
      height: 36px;
      gap: var(--r2m-space-1);
      padding-right: var(--r2m-space-2);
    }
    .character-lines__row--expanded {
      background: color-mix(in srgb, var(--r2m-accent) 8%, transparent);
    }
    .character-lines__toggle {
      flex: 0 0 auto;
      width: 32px;
      height: 32px;
      padding: 4px;
    }
    .character-lines__text {
      flex: 1 1 auto;
      min-width: 0;
      overflow: hidden;
      white-space: nowrap;
      text-overflow: ellipsis;
      text-align: left;
      border: 0;
      background: none;
      padding: 0;
      font: inherit;
      font-style: italic;
      color: var(--r2m-text);
      cursor: pointer;
    }
    .character-lines__text:hover {
      text-decoration: underline;
    }
    .character-lines__context {
      margin-top: var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-md);
      background: var(--r2m-surface-low);
    }
    .character-lines__context-line {
      padding: var(--r2m-space-2) var(--r2m-space-3) 0;
      font-style: italic;
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
  `,
})
export class CharacterLines {
  readonly folder = input.required<string>();
  readonly lines = input.required<readonly CharacterLineDto[]>();
  readonly loading = input(false);

  /** A line's text was clicked: open the reader there. */
  readonly open = output<CharacterLineDto>();

  protected readonly rowHeight = LINE_ROW_HEIGHT;
  protected readonly expandedId = signal<string | null>(null);

  protected readonly expanded = computed(() => {
    const id = this.expandedId();
    return id === null ? null : (this.lines().find((l) => l.itemId === id) ?? null);
  });

  /** Up to ten rows tall, then scrolls. */
  protected readonly viewportHeight = computed(
    () => Math.min(this.lines().length, 10) * LINE_ROW_HEIGHT + 2,
  );

  protected readonly trackLine = (_: number, line: CharacterLineDto) => line.itemId;

  protected toggle(line: CharacterLineDto): void {
    this.expandedId.update((id) => (id === line.itemId ? null : line.itemId));
  }
}
