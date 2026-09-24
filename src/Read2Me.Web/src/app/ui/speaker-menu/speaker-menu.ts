import { ActiveDescendantKeyManager, Highlightable } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  Directive,
  ElementRef,
  OnDestroy,
  booleanAttribute,
  computed,
  inject,
  input,
  output,
  signal,
  viewChildren,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { speakerHue } from '@app/shared/speaker-color';

export interface SpeakerRosterEntry {
  id: string;
  name: string;
  isNarrator?: boolean;
  aliases?: string[];
}

/** One roster row; the key manager highlights it without moving focus out of the search box. */
@Directive({
  selector: '[r2mSpeakerMenuRow]',
})
export class SpeakerMenuRow implements Highlightable {
  readonly entry = input.required<SpeakerRosterEntry>({ alias: 'r2mSpeakerMenuRow' });
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  active = false;

  setActiveStyles(): void {
    this.active = true;
    this.host.nativeElement.classList.add('r2m-speaker-menu__row--active');
  }
  setInactiveStyles(): void {
    this.active = false;
    this.host.nativeElement.classList.remove('r2m-speaker-menu__row--active');
  }
}

/**
 * Speaker picker panel (design §7): roster search, narrator first, Clear speaker, New character…
 * One implementation for item, paragraph and bulk assignment; presentational, works inside a
 * mat-menu (clicks inside the search box do not close the host menu) or standalone.
 */
@Component({
  selector: 'r2m-speaker-menu',
  imports: [MatIconModule, SpeakerMenuRow],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-speaker-menu', '(keydown)': 'onKeydown($event)' },
  template: `
    <label class="r2m-speaker-menu__search">
      <mat-icon aria-hidden="true">search</mat-icon>
      <input
        #search
        type="text"
        class="r2m-speaker-menu__input"
        placeholder="Search characters"
        autocomplete="off"
        aria-label="Search characters"
        [value]="query()"
        (input)="query.set(search.value)"
        (click)="$event.stopPropagation()"
        (keydown)="$event.stopPropagation(); onKeydown($event)"
      />
    </label>
    <div class="r2m-speaker-menu__list" role="listbox" aria-label="Characters">
      @for (entry of filtered(); track entry.id) {
        <button
          type="button"
          role="option"
          class="r2m-speaker-menu__row"
          [class.r2m-speaker-menu__row--selected]="entry.id === selectedId()"
          [attr.aria-selected]="entry.id === selectedId()"
          [r2mSpeakerMenuRow]="entry"
          [style.--r2m-speaker-hue]="hue(entry.id)"
          (click)="choose(entry)"
        >
          <span class="r2m-speaker-menu__dot" aria-hidden="true"></span>
          <span class="r2m-speaker-menu__name">{{ entry.name }}</span>
          @if (entry.isNarrator) {
            <mat-icon class="r2m-speaker-menu__narrator" aria-label="Narrator"
              >auto_stories</mat-icon
            >
          }
        </button>
      } @empty {
        <p class="r2m-speaker-menu__empty">No matches</p>
      }
    </div>
    @if (allowClear() || allowCreate()) {
      <div class="r2m-speaker-menu__footer">
        @if (allowClear()) {
          <button type="button" class="r2m-speaker-menu__action" (click)="clear.emit()">
            <mat-icon aria-hidden="true">person_off</mat-icon><span>Clear speaker</span>
          </button>
        }
        @if (allowCreate()) {
          <button
            type="button"
            class="r2m-speaker-menu__action"
            (click)="create.emit(query().trim())"
          >
            <mat-icon aria-hidden="true">person_add</mat-icon>
            <span>New character{{ query().trim() ? ' “' + query().trim() + '”' : '…' }}</span>
          </button>
        }
      </div>
    }
  `,
  styles: `
    @use 'tokens';
    :host {
      display: flex;
      flex-direction: column;
      width: 280px;
      max-height: 360px;
      font-size: var(--r2m-text-md);
      color: var(--r2m-text);
    }
    .r2m-speaker-menu__search {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2) var(--r2m-space-3);
      border-bottom: 1px solid var(--r2m-outline);
      color: var(--r2m-text-muted);
    }
    .r2m-speaker-menu__search mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .r2m-speaker-menu__input {
      flex: 1 1 auto;
      min-width: 0;
      border: none;
      outline: none;
      background: transparent;
      font: inherit;
      color: var(--r2m-text);
    }
    .r2m-speaker-menu__list {
      flex: 1 1 auto;
      overflow-y: auto;
      padding: var(--r2m-space-1) 0;
    }
    .r2m-speaker-menu__row,
    .r2m-speaker-menu__action {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      width: 100%;
      min-height: 32px;
      padding: 0 var(--r2m-space-3);
      border: none;
      background: transparent;
      font: inherit;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }
    .r2m-speaker-menu__row:hover,
    .r2m-speaker-menu__action:hover,
    .r2m-speaker-menu__row--active {
      background: var(--r2m-surface-high);
    }
    .r2m-speaker-menu__row--selected {
      font-weight: 600;
    }
    .r2m-speaker-menu__row:focus-visible,
    .r2m-speaker-menu__action:focus-visible {
      outline: 2px solid var(--r2m-accent);
      outline-offset: -2px;
    }
    .r2m-speaker-menu__dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      flex: 0 0 auto;
      @include tokens.speaker-colors;
      background: var(--r2m-speaker-fg);
    }
    .r2m-speaker-menu__name {
      flex: 1 1 auto;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .r2m-speaker-menu__narrator,
    .r2m-speaker-menu__action mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
      color: var(--r2m-text-muted);
    }
    .r2m-speaker-menu__empty {
      margin: 0;
      padding: var(--r2m-space-3);
      color: var(--r2m-text-muted);
      text-align: center;
    }
    .r2m-speaker-menu__footer {
      border-top: 1px solid var(--r2m-outline);
      padding: var(--r2m-space-1) 0;
    }
  `,
})
export class SpeakerMenu implements OnDestroy {
  readonly roster = input.required<readonly SpeakerRosterEntry[]>();
  readonly selectedId = input<string>();
  readonly allowClear = input(true, { transform: booleanAttribute });
  readonly allowCreate = input(true, { transform: booleanAttribute });

  readonly pick = output<string>();
  readonly clear = output<void>();
  readonly create = output<string>();

  protected readonly query = signal('');
  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.roster()
      .filter(
        (e) =>
          !q ||
          e.name.toLowerCase().includes(q) ||
          (e.aliases ?? []).some((a) => a.toLowerCase().includes(q)),
      )
      .sort(
        (a, b) =>
          Number(!!b.isNarrator) - Number(!!a.isNarrator) ||
          a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      );
  });

  private readonly rows = viewChildren(SpeakerMenuRow);
  private keyManager?: ActiveDescendantKeyManager<SpeakerMenuRow>;
  private keyManagerRows?: readonly SpeakerMenuRow[];

  /**
   * Key manager over the currently rendered rows, rebuilt when filtering changes the row set.
   * Built on demand (not in an effect): the view-query signal must not be read inside a reactive
   * context that also mutates row styling, or the effect re-runs indefinitely.
   */
  private manager(): ActiveDescendantKeyManager<SpeakerMenuRow> {
    const rows = this.rows();
    if (this.keyManager && this.keyManagerRows === rows) return this.keyManager;
    this.keyManager?.destroy();
    this.keyManagerRows = rows;
    this.keyManager = new ActiveDescendantKeyManager(rows).withWrap().withVerticalOrientation();
    if (rows.length) this.keyManager.setFirstItemActive();
    return this.keyManager;
  }

  ngOnDestroy(): void {
    this.keyManager?.destroy();
  }

  protected hue(id: string): number {
    return speakerHue(id);
  }

  protected choose(entry: SpeakerRosterEntry): void {
    this.pick.emit(entry.id);
  }

  protected onKeydown(event: KeyboardEvent): void {
    const manager = this.manager();
    if (event.key === 'Enter') {
      const active = manager.activeItem;
      if (active) {
        event.preventDefault();
        this.choose(active.entry());
      }
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      manager.onKeydown(event);
    }
  }
}
