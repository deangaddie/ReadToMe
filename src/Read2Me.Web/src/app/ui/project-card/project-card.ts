import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ProjectSummary, workspaceUrl } from '@app/api';

/**
 * Shelf card (design §6.1): 3:4 cover (the project's image via `/workspace`, else generated
 * placeholder art), title, author, the audio pipeline bar or "Not read in yet", and an overflow
 * menu with Open / Delete. Clicking the cover or title emits `open`; the card never navigates itself.
 */
@Component({
  selector: 'r2m-project-card',
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'r2m-project-card', '[attr.data-folder]': 'project().folderName' },
  template: `
    <button
      type="button"
      class="r2m-project-card__cover"
      [style.background]="coverUrl() ? null : placeholderArt()"
      [attr.aria-label]="'Open ' + project().title"
      (click)="open.emit()"
    >
      @if (coverUrl(); as url) {
        <img class="r2m-project-card__image" [src]="url" [alt]="project().title + ' cover'" />
      } @else {
        <span class="r2m-project-card__initials" aria-hidden="true">{{ initials() }}</span>
      }
    </button>

    <div class="r2m-project-card__body">
      <div class="r2m-project-card__heading">
        <button
          type="button"
          class="r2m-project-card__title"
          [matTooltip]="project().title"
          (click)="open.emit()"
        >
          {{ project().title }}
        </button>
        <button
          mat-icon-button
          type="button"
          class="r2m-project-card__more"
          [matMenuTriggerFor]="menu"
          [attr.aria-label]="'Actions for ' + project().title"
        >
          <mat-icon>more_vert</mat-icon>
        </button>
        <mat-menu #menu="matMenu">
          <button mat-menu-item type="button" class="r2m-project-card__open" (click)="open.emit()">
            <mat-icon>open_in_new</mat-icon><span>Open</span>
          </button>
          <button
            mat-menu-item
            type="button"
            class="r2m-project-card__delete"
            (click)="delete.emit()"
          >
            <mat-icon>delete</mat-icon><span>Delete</span>
          </button>
        </mat-menu>
      </div>
      <p class="r2m-project-card__author">{{ project().author || 'Unknown author' }}</p>

      @if (project().audioItemTotal > 0) {
        <div
          class="r2m-project-card__progress"
          role="progressbar"
          [attr.aria-valuenow]="percent()"
          aria-valuemin="0"
          aria-valuemax="100"
          [matTooltip]="progressTooltip()"
        >
          <div class="r2m-project-card__bar">
            <div class="r2m-project-card__fill" [style.width.%]="percent()"></div>
          </div>
          <span class="r2m-project-card__percent">{{ percent() }}%</span>
        </div>
      } @else {
        <p class="r2m-project-card__unread">Not read in yet</p>
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      border: 1px solid var(--r2m-outline);
      border-radius: var(--r2m-radius-lg);
      background: var(--r2m-surface-low);
      overflow: hidden;
      transition: box-shadow 120ms ease;
    }
    :host(:hover) {
      box-shadow: var(--r2m-shadow-2);
    }
    .r2m-project-card__cover {
      position: relative;
      display: grid;
      place-items: center;
      aspect-ratio: 3 / 4;
      width: 100%;
      padding: 0;
      border: 0;
      cursor: pointer;
      color: #fff;
      background: var(--r2m-surface);
      overflow: hidden;
    }
    .r2m-project-card__image {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }
    .r2m-project-card__initials {
      font-size: var(--r2m-text-2xl);
      font-weight: 700;
      letter-spacing: 0.04em;
      opacity: 0.9;
      text-shadow: 0 1px 2px rgb(0 0 0 / 30%);
    }
    .r2m-project-card__body {
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-1);
      padding: var(--r2m-space-3) var(--r2m-space-3) var(--r2m-space-3);
    }
    .r2m-project-card__heading {
      display: flex;
      align-items: flex-start;
      gap: var(--r2m-space-1);
    }
    .r2m-project-card__title {
      flex: 1 1 auto;
      min-width: 0;
      margin: 0;
      padding: 0;
      border: 0;
      background: none;
      cursor: pointer;
      text-align: left;
      font: inherit;
      font-size: var(--r2m-text-md);
      font-weight: 600;
      line-height: var(--r2m-line-tight);
      color: var(--r2m-text);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .r2m-project-card__title:hover {
      color: var(--r2m-accent);
    }
    .r2m-project-card__more {
      flex: 0 0 auto;
      margin: -8px -8px 0 0;
    }
    .r2m-project-card__author,
    .r2m-project-card__unread {
      margin: 0;
      font-size: var(--r2m-text-sm);
      color: var(--r2m-text-muted);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .r2m-project-card__unread {
      margin-top: var(--r2m-space-1);
      font-style: italic;
    }
    .r2m-project-card__progress {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      margin-top: var(--r2m-space-1);
    }
    .r2m-project-card__bar {
      flex: 1 1 auto;
      height: 6px;
      border-radius: 3px;
      background: color-mix(in srgb, var(--r2m-text-muted) 20%, transparent);
      overflow: hidden;
    }
    .r2m-project-card__fill {
      height: 100%;
      border-radius: inherit;
      background: var(--r2m-accent);
      transition: width 200ms ease;
    }
    .r2m-project-card__percent {
      flex: 0 0 auto;
      font-size: var(--r2m-text-xs);
      color: var(--r2m-text-muted);
      font-variant-numeric: tabular-nums;
    }
    .r2m-project-card__delete {
      color: var(--r2m-status-error);
    }
  `,
})
export class ProjectCard {
  readonly project = input.required<ProjectSummary>();

  readonly open = output<void>();
  readonly delete = output<void>();

  readonly coverUrl = computed(() => {
    const p = this.project();
    return p.coverImage ? workspaceUrl(p.folderName, p.coverImage) : null;
  });

  readonly initials = computed(() => projectInitials(this.project().title));

  readonly placeholderArt = computed(() => placeholderGradient(this.project().folderName));

  readonly percent = computed(() => {
    const p = this.project();
    return p.audioItemTotal === 0 ? 0 : Math.floor((100 * p.audioItemDone) / p.audioItemTotal);
  });

  readonly progressTooltip = computed(() => {
    const p = this.project();
    return `Audio: ${p.audioItemDone} of ${p.audioItemTotal} items`;
  });
}

/**
 * First letter or digit of the first two words that have one, upper-cased; a single-word title
 * gives one letter and punctuation-only words ("(", "&") are skipped.
 */
export function projectInitials(title: string): string {
  return title
    .split(/\s+/)
    .map((w) => /\p{L}|\p{N}/u.exec(w)?.[0])
    .filter((c): c is string => c !== undefined)
    .slice(0, 2)
    .map((c) => c.toUpperCase())
    .join('');
}

/** Stable two-tone gradient from the folder name, so a project keeps its colour across reloads. */
export function placeholderGradient(seed: string): string {
  let hash = 0;
  for (const ch of seed) hash = (hash * 31 + ch.codePointAt(0)!) >>> 0;
  const hue = hash % 360;
  return `linear-gradient(135deg, hsl(${hue} 45% 48%), hsl(${(hue + 40) % 360} 50% 30%))`;
}
