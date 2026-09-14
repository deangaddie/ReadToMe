import {
  ChangeDetectionStrategy,
  Component,
  booleanAttribute,
  computed,
  input,
  output,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { speakerHue } from '@app/shared/speaker-color';
import { SpeakerMenu, SpeakerRosterEntry } from '../speaker-menu/speaker-menu';

export type SpeakerChipState = 'named' | 'unknown' | 'mixed' | 'narration' | 'narrator-linked';

/**
 * Speaker chip (design §7): a speaker name with a stable per-character hue. States: Unknown
 * (dashed), Mixed, Narration, and a linked narrator. When `interactive` it is a button that emits
 * `open`; give it a `roster` and it opens `r2m-speaker-menu` itself and relays pick/clear/create.
 */
@Component({
  selector: 'r2m-speaker-chip',
  imports: [NgTemplateOutlet, MatIconModule, MatMenuModule, SpeakerMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'r2m-speaker-chip',
    '[class]': '"r2m-speaker-chip--" + state()',
    '[class.r2m-speaker-chip--compact]': 'compact()',
    '[class.r2m-speaker-chip--interactive]': 'interactive()',
    '[style.--r2m-speaker-hue]': 'hue()',
  },
  template: `
    @if (interactive() && roster()) {
      <button
        type="button"
        class="r2m-speaker-chip__body"
        [matMenuTriggerFor]="menu"
        (click)="open.emit()"
      >
        <ng-container *ngTemplateOutlet="content" />
      </button>
      <mat-menu #menu="matMenu" class="r2m-speaker-chip__menu">
        <r2m-speaker-menu
          [roster]="roster()!"
          [selectedId]="characterId()"
          (pick)="pick.emit($event)"
          (clear)="clear.emit()"
          (create)="create.emit($event)"
        />
      </mat-menu>
    } @else if (interactive()) {
      <button type="button" class="r2m-speaker-chip__body" (click)="open.emit()">
        <ng-container *ngTemplateOutlet="content" />
      </button>
    } @else {
      <span class="r2m-speaker-chip__body"><ng-container *ngTemplateOutlet="content" /></span>
    }

    <ng-template #content>
      @switch (state()) {
        @case ('unknown') {
          <mat-icon class="r2m-speaker-chip__icon" aria-hidden="true">question_mark</mat-icon>
        }
        @case ('mixed') {
          <mat-icon class="r2m-speaker-chip__icon" aria-hidden="true">call_split</mat-icon>
        }
        @case ('narration') {
          <mat-icon class="r2m-speaker-chip__icon" aria-hidden="true">auto_stories</mat-icon>
        }
        @default {
          <span class="r2m-speaker-chip__dot" aria-hidden="true"></span>
        }
      }
      <span class="r2m-speaker-chip__name">{{ label() }}</span>
      @if (state() === 'narrator-linked') {
        <mat-icon class="r2m-speaker-chip__link" aria-label="Linked narrator">link</mat-icon>
      }
    </ng-template>
  `,
  styles: `
    @use 'tokens';
    :host {
      display: inline-flex;
      vertical-align: middle;
      @include tokens.speaker-colors;
      --_fg: var(--r2m-speaker-fg);
      --_bg: var(--r2m-speaker-bg);
      --_border: transparent;
    }
    .r2m-speaker-chip__body {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      height: 24px;
      padding: 0 var(--r2m-space-2);
      border: 1px solid var(--_border);
      border-radius: var(--r2m-radius-pill);
      background: var(--_bg);
      color: var(--_fg);
      font: inherit;
      font-size: var(--r2m-text-sm);
      font-weight: 500;
      line-height: 1;
      white-space: nowrap;
      cursor: default;
    }
    :host(.r2m-speaker-chip--compact) .r2m-speaker-chip__body {
      height: 20px;
      font-size: var(--r2m-text-xs);
      padding: 0 6px;
    }
    :host(.r2m-speaker-chip--interactive) .r2m-speaker-chip__body {
      cursor: pointer;
    }
    :host(.r2m-speaker-chip--interactive) .r2m-speaker-chip__body:hover {
      box-shadow: var(--r2m-shadow-1);
    }
    .r2m-speaker-chip__body:focus-visible {
      outline: 2px solid var(--r2m-accent);
      outline-offset: 1px;
    }
    :host(.r2m-speaker-chip--unknown) {
      --_fg: var(--r2m-status-warn);
      --_bg: transparent;
      --_border: var(--r2m-status-warn);
    }
    :host(.r2m-speaker-chip--unknown) .r2m-speaker-chip__body {
      border-style: dashed;
    }
    :host(.r2m-speaker-chip--mixed) {
      --_fg: var(--r2m-status-neutral);
      --_bg: var(--r2m-status-neutral-soft);
    }
    :host(.r2m-speaker-chip--narration) {
      --_fg: var(--r2m-text-muted);
      --_bg: var(--r2m-surface-low);
      --_border: var(--r2m-outline);
    }
    .r2m-speaker-chip__dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--_fg);
    }
    .r2m-speaker-chip__icon,
    .r2m-speaker-chip__link {
      font-size: 14px;
      width: 14px;
      height: 14px;
      color: inherit;
    }
    .r2m-speaker-chip__link {
      opacity: 0.8;
    }
  `,
})
export class SpeakerChip {
  readonly name = input('');
  readonly characterId = input<string>();
  readonly state = input<SpeakerChipState>('named');
  readonly interactive = input(false, { transform: booleanAttribute });
  readonly compact = input(false, { transform: booleanAttribute });
  readonly roster = input<readonly SpeakerRosterEntry[]>();

  readonly open = output<void>();
  readonly pick = output<string>();
  readonly clear = output<void>();
  readonly create = output<string>();

  protected readonly hue = computed(() => speakerHue(this.characterId() ?? this.name() ?? '?'));

  protected readonly label = computed(() => {
    const name = this.name();
    switch (this.state()) {
      case 'unknown':
        return name || 'Unknown';
      case 'mixed':
        return name || 'Mixed';
      case 'narration':
        return name || 'Narration';
      default:
        return name;
    }
  });
}
