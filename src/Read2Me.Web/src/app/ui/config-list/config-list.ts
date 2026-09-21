import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { StatusChip } from '@app/ui/status-chip/status-chip';

export interface ConfigListItem {
  id: string;
  name: string;
  subtitle?: string;
  isActive?: boolean;
  badge?: string;
}

/**
 * Master list for the settings master–detail template (design §6.4, §7): searchable rows with an
 * Active marker and a per-row overflow menu (Make active / Duplicate / Delete), plus New.
 */
@Component({
  selector: 'r2m-config-list',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, StatusChip],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-config-list' },
  template: `
    <header class="r2m-config-list__head">
      <label class="r2m-config-list__search">
        <mat-icon aria-hidden="true">search</mat-icon>
        <input
          type="search"
          placeholder="Search"
          aria-label="Search configurations"
          [value]="query()"
          (input)="query.set($any($event.target).value)"
        />
      </label>
      <button mat-flat-button type="button" [disabled]="busy()" (click)="create.emit()">
        <mat-icon>add</mat-icon>New
      </button>
    </header>

    @if (filtered().length === 0) {
      <p class="r2m-config-list__empty">
        {{ items().length === 0 ? 'No configurations yet.' : 'Nothing matches your search.' }}
      </p>
    } @else {
      <ul class="r2m-config-list__rows" role="listbox" aria-label="Configurations">
        @for (item of filtered(); track item.id) {
          <li
            class="r2m-config-list__row"
            role="option"
            [attr.aria-selected]="item.id === selectedId()"
            [class.r2m-config-list__row--selected]="item.id === selectedId()"
          >
            <button type="button" class="r2m-config-list__main" (click)="select.emit(item.id)">
              <span class="r2m-config-list__name">{{ item.name }}</span>
              @if (item.subtitle) {
                <span class="r2m-config-list__subtitle">{{ item.subtitle }}</span>
              }
            </button>
            @if (item.badge) {
              <span class="r2m-config-list__badge">{{ item.badge }}</span>
            }
            @if (item.isActive) {
              <r2m-status-chip status="ok" [label]="activeLabel()" compact />
            }
            <button
              mat-icon-button
              type="button"
              [matMenuTriggerFor]="rowMenu"
              [matMenuTriggerData]="{ item: item }"
              [disabled]="busy()"
              [attr.aria-label]="'Actions for ' + item.name"
            >
              <mat-icon>more_vert</mat-icon>
            </button>
          </li>
        }
      </ul>
    }

    <mat-menu #rowMenu="matMenu">
      <ng-template matMenuContent let-item="item">
        <button
          mat-menu-item
          type="button"
          [disabled]="item.isActive"
          (click)="makeActive.emit(item.id)"
        >
          <mat-icon>check_circle</mat-icon><span>Make {{ activeLabel().toLowerCase() }}</span>
        </button>
        <button mat-menu-item type="button" (click)="duplicate.emit(item.id)">
          <mat-icon>content_copy</mat-icon><span>Duplicate</span>
        </button>
        <button
          mat-menu-item
          type="button"
          class="r2m-config-list__delete"
          (click)="delete.emit(item.id)"
        >
          <mat-icon>delete</mat-icon><span>Delete</span>
        </button>
      </ng-template>
    </mat-menu>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
    }
    .r2m-config-list__head {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: 0 0 var(--r2m-space-2);
    }
    .r2m-config-list__search {
      flex: 1;
      display: flex;
      align-items: center;
      gap: var(--r2m-space-1);
      height: 36px;
      padding: 0 var(--r2m-space-2);
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-pill);
      color: var(--r2m-text-muted);
      background: var(--r2m-surface);
    }
    .r2m-config-list__search input {
      flex: 1;
      min-width: 0;
      border: 0;
      background: transparent;
      color: var(--r2m-text);
      font: inherit;
      outline: none;
    }
    .r2m-config-list__search mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .r2m-config-list__empty {
      margin: var(--r2m-space-4) 0;
      text-align: center;
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-sm);
    }
    .r2m-config-list__rows {
      list-style: none;
      margin: 0;
      padding: 0;
      overflow: auto;
    }
    .r2m-config-list__row {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: 0 var(--r2m-space-1) 0 0;
      border-radius: var(--r2m-radius-md);
    }
    .r2m-config-list__row:hover {
      background: var(--r2m-surface-low);
    }
    .r2m-config-list__row--selected {
      background: var(--r2m-nav-active-bg);
      color: var(--r2m-nav-active-fg);
    }
    .r2m-config-list__main {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      min-width: 0;
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border: 0;
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
    .r2m-config-list__name {
      font-weight: 500;
    }
    .r2m-config-list__subtitle {
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
    }
    .r2m-config-list__badge {
      font-size: var(--r2m-text-xs);
      padding: 2px var(--r2m-space-2);
      border-radius: var(--r2m-radius-pill);
      background: var(--r2m-surface-high);
      color: var(--r2m-text-muted);
    }
    .r2m-config-list__delete {
      color: var(--r2m-status-error);
    }
  `,
})
export class ConfigList {
  readonly items = input.required<ConfigListItem[]>();
  readonly selectedId = input<string | null>(null);
  readonly busy = input(false, { transform: booleanAttribute });
  /** What the area calls its selected config: "Active" for providers, "Default" for LLM. */
  readonly activeLabel = input('Active');

  // eslint-disable-next-line @angular-eslint/no-output-native -- name fixed by the design vocabulary (design §7)
  readonly select = output<string>();
  readonly makeActive = output<string>();
  readonly duplicate = output<string>();
  readonly delete = output<string>();
  readonly create = output<void>();

  protected readonly query = signal('');

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return this.items();
    return this.items().filter(
      (i) => i.name.toLowerCase().includes(q) || (i.subtitle ?? '').toLowerCase().includes(q),
    );
  });
}
