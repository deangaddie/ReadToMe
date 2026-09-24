import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { PromptService } from '@app/ui/text-prompt-dialog/text-prompt-dialog';
import { BookEditor } from './book-editor';
import { commandFor } from './node-menu-commands';
import {
  ActionEntryId,
  MenuEntry,
  MenuEntryId,
  NodeMenuTarget,
  isActionEntry,
  menuEntries,
} from './node-menu-entries';

/**
 * The node menu (ticket 11, design §6.3): one component for every level of the book, the entry set
 * decided by {@link menuEntries} from the target's kind and position. A chosen entry runs its
 * dialog flow and posts the command through {@link BookEditor}; nothing here patches the rows.
 * The selection entries (ticket 12) are not commands: they are emitted as `action` for the tree.
 * Disabled by the caller for a busy row, and by the editor while stale or mid-write (design §8).
 */
@Component({
  selector: 'r2m-node-menu',
  imports: [MatButtonModule, MatDividerModule, MatIconModule, MatMenuModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-node-menu' },
  template: `
    <button
      mat-icon-button
      type="button"
      class="r2m-node-menu__trigger"
      [matMenuTriggerFor]="menu"
      [disabled]="isDisabled()"
      [attr.aria-label]="label()"
      (click)="$event.stopPropagation()"
    >
      <mat-icon>more_vert</mat-icon>
    </button>

    <mat-menu #menu="matMenu" class="r2m-node-menu__panel">
      @for (entry of main(); track entry.id) {
        @if (entry.group !== previous(entry)) {
          <mat-divider></mat-divider>
        }
        <button
          mat-menu-item
          type="button"
          [class.r2m-node-menu__destructive]="entry.destructive"
          [attr.data-entry]="entry.id"
          (click)="choose(entry.id)"
        >
          <mat-icon>{{ entry.icon }}</mat-icon>
          <span>{{ entry.label }}</span>
        </button>
      }
      @if (pausesBefore().length) {
        <mat-divider></mat-divider>
        <button mat-menu-item type="button" [matMenuTriggerFor]="before" data-entry="pause-before">
          <mat-icon>pause</mat-icon>
          <span>Insert pause before</span>
        </button>
        <button mat-menu-item type="button" [matMenuTriggerFor]="after" data-entry="pause-after">
          <mat-icon>pause</mat-icon>
          <span>Insert pause after</span>
        </button>
      }
      @if (deleteEntry(); as entry) {
        <mat-divider></mat-divider>
        <button
          mat-menu-item
          type="button"
          class="r2m-node-menu__destructive"
          [attr.data-entry]="entry.id"
          (click)="choose(entry.id)"
        >
          <mat-icon>{{ entry.icon }}</mat-icon>
          <span>{{ entry.label }}</span>
        </button>
      }
    </mat-menu>

    <mat-menu #before="matMenu">
      @for (entry of pausesBefore(); track entry.id) {
        <button mat-menu-item type="button" [attr.data-entry]="entry.id" (click)="choose(entry.id)">
          {{ entry.label }}
        </button>
      }
    </mat-menu>
    <mat-menu #after="matMenu">
      @for (entry of pausesAfter(); track entry.id) {
        <button mat-menu-item type="button" [attr.data-entry]="entry.id" (click)="choose(entry.id)">
          {{ entry.label }}
        </button>
      }
    </mat-menu>
  `,
  styles: `
    :host {
      display: inline-flex;
      flex: 0 0 auto;
    }
    .r2m-node-menu__trigger {
      --mdc-icon-button-state-layer-size: 28px;
      width: 28px;
      height: 28px;
      padding: 2px;
      color: var(--r2m-text-muted);
    }
    .r2m-node-menu__trigger mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
  `,
})
export class NodeMenu {
  private readonly editor = inject(BookEditor);
  private readonly prompt = inject(PromptService);
  private readonly confirm = inject(ConfirmService);

  readonly target = input.required<NodeMenuTarget>();
  /** The row is queued or processing: the server would refuse, so the menu is off. */
  readonly disabled = input(false, { transform: booleanAttribute });
  /** A selection entry was chosen (tree nodes only). */
  readonly action = output<ActionEntryId>();

  protected readonly isDisabled = computed(() => this.disabled() || this.editor.locked());
  protected readonly label = computed(
    () => `Actions for ${this.target().text?.trim() || this.target().kind}`,
  );

  private readonly entries = computed(() => menuEntries(this.target()));
  /** Everything but the pause submenus and the delete, which render in their own places. */
  protected readonly main = computed(() =>
    this.entries().filter((e) => !e.group.startsWith('pause-') && e.group !== 'delete'),
  );
  protected readonly pausesBefore = computed(() =>
    this.entries().filter((e) => e.group === 'pause-before'),
  );
  protected readonly pausesAfter = computed(() =>
    this.entries().filter((e) => e.group === 'pause-after'),
  );
  protected readonly deleteEntry = computed(
    () => this.entries().find((e) => e.group === 'delete') ?? null,
  );

  /** The group of the entry before this one, so a divider separates groups. */
  protected previous(entry: MenuEntry): MenuEntry['group'] {
    const main = this.main();
    const index = main.indexOf(entry);
    return index <= 0 ? entry.group : main[index - 1]!.group;
  }

  protected async choose(id: MenuEntryId): Promise<void> {
    if (isActionEntry(id)) {
      this.action.emit(id);
      return;
    }
    const command = await commandFor(id, this.target(), {
      text: (o) => this.prompt.text(o),
      confirm: (o) => this.confirm.confirm(o),
    });
    if (command) await this.editor.run(command);
  }
}
