import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { ProjectStore } from '../project/project-store';
import { BookStore, WINDOW_CAP } from './book-store';
import { MeasuredScrollDirective } from './measured-scroll';
import { ParagraphRow } from './paragraph-row';
import {
  READER_MODES,
  ReaderMode,
  ReaderRow,
  RowContext,
  buildRows,
  parseMode,
} from './reader-rows';
import { StructureTree } from './structure-tree';

/** Load the adjacent chapter when the viewport is this close to either end. */
const EDGE_PX = 600;

const MODE_LABELS: Record<ReaderMode, string> = {
  read: 'Read',
  speakers: 'Speakers',
  audio: 'Audio',
};

/**
 * `/projects/{folder}/book` (ticket 10, design §6.3): the structure tree beside a virtual-scrolled
 * window of adjacent chapters. The mode (`?mode=`) changes what each row shows, never where it is.
 * Read-only in this slice: no selection, no menus.
 */
@Component({
  selector: 'app-book-page',
  imports: [
    ScrollingModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    RouterLink,
    EmptyState,
    MeasuredScrollDirective,
    ParagraphRow,
    StructureTree,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="book__toolbar" role="toolbar" aria-label="Reader">
      <button
        mat-icon-button
        type="button"
        [attr.aria-label]="treeOpen() ? 'Hide structure' : 'Show structure'"
        [attr.aria-expanded]="treeOpen()"
        (click)="treeOpen.set(!treeOpen())"
      >
        <mat-icon>{{ treeOpen() ? 'left_panel_close' : 'left_panel_open' }}</mat-icon>
      </button>
      <mat-button-toggle-group
        hideSingleSelectionIndicator
        aria-label="Reader mode"
        [value]="store.mode()"
        (change)="setMode($event.value)"
      >
        @for (m of modes; track m) {
          <mat-button-toggle [value]="m">{{ modeLabels[m] }}</mat-button-toggle>
        }
      </mat-button-toggle-group>
    </div>

    @if (store.stale(); as reason) {
      <div class="book__stale" role="alert">
        <mat-icon aria-hidden="true">sync_problem</mat-icon>
        <span>This view may be out of date: {{ reason }}</span>
        <button mat-button type="button" (click)="refresh()">Refresh</button>
      </div>
    }

    @if (noContent()) {
      <r2m-empty-state
        icon="menu_book"
        headline="The book has not been read in yet"
        hint="Read it in from the project overview."
      >
        <a mat-flat-button action [routerLink]="['..']">Go to overview</a>
      </r2m-empty-state>
    } @else {
      <div class="book__body" [class.book__body--tree-hidden]="!treeOpen()">
        @if (treeOpen()) {
          <nav class="book__tree" aria-label="Structure">
            <app-structure-tree
              [nodes]="store.tree()"
              [statuses]="project.nodes()"
              [currentChapterId]="store.currentChapterId()"
              [expandedIds]="store.expanded()"
              (expandedChange)="store.setExpanded($event.node, $event.expanded)"
              (selectChapter)="openChapter($event)"
            />
          </nav>
        }

        <section class="book__reader" aria-label="Chapter">
          @if (rows().length === 0) {
            <div class="book__skeleton" aria-busy="true" aria-label="Loading chapter">
              @for (n of skeleton; track n) {
                <div class="book__skeleton-line"></div>
              }
            </div>
          }
          <cdk-virtual-scroll-viewport
            class="book__viewport"
            [class.book__viewport--empty]="rows().length === 0"
            [r2mMeasuredScroll]="keys()"
            (scrolledIndexChange)="onTopRow($event)"
          >
            <div
              *cdkVirtualFor="let row of rows(); trackBy: trackRow"
              class="book__row"
              [attr.data-row-key]="row.key"
            >
              @switch (row.kind) {
                @case ('chapter') {
                  <h2 class="book__chapter">{{ row.title }}</h2>
                }
                @case ('paragraph') {
                  <r2m-paragraph [paragraph]="row.paragraph" [ctx]="ctx()" />
                }
                @case ('pause') {
                  <div class="book__pause" role="separator" [attr.aria-label]="row.label">
                    <span class="book__pause-label">{{ row.label }}</span>
                  </div>
                }
              }
            </div>
          </cdk-virtual-scroll-viewport>
        </section>
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 480px;
      padding-top: var(--r2m-space-3);
      box-sizing: border-box;
    }
    .book__toolbar {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-3);
      padding-bottom: var(--r2m-space-3);
    }
    .book__stale {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-1) var(--r2m-space-3);
      margin-bottom: var(--r2m-space-2);
      border-radius: var(--r2m-radius-md);
      color: var(--r2m-status-warn);
      background: var(--r2m-status-warn-soft);
    }
    .book__body {
      flex: 1 1 auto;
      min-height: 0;
      display: grid;
      grid-template-columns: 280px minmax(0, 1fr);
      gap: var(--r2m-space-4);
    }
    .book__body--tree-hidden {
      grid-template-columns: minmax(0, 1fr);
    }
    .book__tree {
      overflow: auto;
      border-right: 1px solid var(--r2m-outline);
      padding-right: var(--r2m-space-2);
    }
    .book__reader {
      position: relative;
      min-height: 0;
      display: flex;
      flex-direction: column;
    }
    .book__viewport {
      flex: 1 1 auto;
      min-height: 0;
    }
    .book__viewport--empty {
      visibility: hidden;
    }
    .book__row {
      max-width: 72ch;
      padding-right: var(--r2m-space-3);
    }
    .book__chapter {
      margin: 0;
      padding: var(--r2m-space-6) 0 var(--r2m-space-2);
      font-size: var(--r2m-text-xl);
      font-weight: 600;
      border-bottom: 1px solid var(--r2m-outline);
    }
    .book__pause {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: var(--r2m-space-2) 0 var(--r2m-space-2) var(--r2m-space-8);
      color: var(--r2m-text-muted);
      font-size: var(--r2m-text-xs);
    }
    .book__pause::after {
      content: '';
      flex: 1 1 auto;
      border-top: 1px dashed var(--r2m-outline);
    }
    .book__skeleton {
      position: absolute;
      inset: 0;
      display: flex;
      flex-direction: column;
      gap: var(--r2m-space-3);
      padding-top: var(--r2m-space-6);
      max-width: 72ch;
    }
    .book__skeleton-line {
      height: 44px;
      border-radius: var(--r2m-radius-md);
      background: color-mix(in srgb, var(--r2m-text-muted) 12%, transparent);
    }
  `,
})
export class BookPage {
  protected readonly project = inject(ProjectStore);
  protected readonly store = inject(BookStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly injector = inject(Injector);

  /** `?mode=` (bound by `withComponentInputBinding`). */
  readonly mode = input<string>();
  /** `?chapter=`: open at this chapter instead of the first. */
  readonly chapter = input<string>();

  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  protected readonly modes = READER_MODES;
  protected readonly modeLabels = MODE_LABELS;
  protected readonly skeleton = [1, 2, 3, 4, 5, 6];
  protected readonly treeOpen = signal(true);

  protected readonly noContent = computed(() => this.store.overview()?.hasContent === false);
  protected readonly rows = computed(() => buildRows(this.store.chapters(), this.store.mode()));
  protected readonly keys = computed(() => this.rows().map((r) => r.key));

  protected readonly ctx = computed<RowContext>(() => ({
    folder: this.store.folder() ?? '',
    mode: this.store.mode(),
    speakers: this.store.speakers(),
    paragraphStatus: this.project.paragraphs(),
    itemStatus: this.project.items(),
    voices: this.store.itemVoices(),
    reviews: this.store.reviews(),
  }));

  protected readonly trackRow = (_: number, row: ReaderRow) => row.key;

  constructor() {
    effect(() => {
      const mode = parseMode(this.mode());
      untracked(() => this.store.setMode(mode));
    });

    effect(() => {
      const folder = this.project.folder();
      if (!folder) return;
      untracked(() => void this.start(folder));
    });

    effect(() => {
      const request = this.store.scrollRequest();
      if (!request) return;
      untracked(() => this.scrollToChapter(request.chapterId));
    });

    const edges = setInterval(() => this.checkEdges(), 250);
    inject(DestroyRef).onDestroy(() => clearInterval(edges));
  }

  protected setMode(mode: ReaderMode): void {
    this.store.setMode(mode);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { mode: mode === 'read' ? null : mode },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected openChapter(chapterId: string): void {
    void this.store.openChapter(chapterId);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { chapter: chapterId },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  protected onTopRow(index: number): void {
    const row = this.rows()[index];
    if (row) this.store.setCurrentChapter(row.chapterId);
  }

  protected refresh(): void {
    void this.store.refresh();
  }

  private async start(folder: string): Promise<void> {
    await this.store.open(folder);
    if (this.store.window().length > 0 || this.noContent()) return;
    const chapter = this.chapter();
    if (chapter) await this.store.openChapter(chapter);
    else await this.store.openFirstChapter();
  }

  private scrollToChapter(chapterId: string): void {
    afterNextRender(
      () => {
        const index = this.rows().findIndex(
          (r) => r.kind === 'chapter' && r.chapterId === chapterId,
        );
        const viewport = this.viewport();
        if (index < 0 || !viewport) return;
        viewport.scrollToIndex(index);
        // The chapter before is needed to scroll up from the top of this one.
        void this.store.loadPrevious();
      },
      { injector: this.injector },
    );
  }

  /**
   * Loads the neighbouring chapter when the viewport nears an end. Polled rather than tied to
   * scroll events: a chapter shorter than the viewport never scrolls, and wheel input at the very
   * top fires no scroll event at all.
   */
  private checkEdges(): void {
    const viewport = this.viewport();
    if (!viewport || this.rows().length === 0) return;
    const top = viewport.measureScrollOffset('top');
    const bottom = viewport.measureScrollOffset('bottom');
    // A full window that still fits on screen would cycle chapters in and out; leave it be.
    if (top + bottom < 2 * EDGE_PX && this.store.window().length >= WINDOW_CAP) return;
    if (bottom < EDGE_PX) void this.store.loadNext();
    if (top < EDGE_PX) void this.store.loadPrevious();
  }
}
