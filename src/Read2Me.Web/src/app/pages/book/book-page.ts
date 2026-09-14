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
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AttributionApi, BookApi, BookCommand, ManualImportRequest, toApiError } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import { ConfirmService } from '@app/ui/confirm-dialog/confirm-dialog';
import { EmptyState } from '@app/ui/empty-state/empty-state';
import { SpeakerMenu } from '@app/ui/speaker-menu/speaker-menu';
import { ToastService } from '@app/ui/toast/toast.service';
import { firstValueFrom } from 'rxjs';
import { ProjectStore } from '../project/project-store';
import { BookEditor } from './book-editor';
import { BookStore, WINDOW_CAP } from './book-store';
import { TreeNode } from './book-tree';
import { ManualRereadDialog } from './manual-reread-dialog';
import { MeasuredScrollDirective } from './measured-scroll';
import { NodeMenu } from './node-menu';
import { NodeMenuTarget } from './node-menu-entries';
import { ParagraphRow } from './paragraph-row';
import {
  READER_MODES,
  ReaderMode,
  ReaderRow,
  RowContext,
  buildRows,
  parseMode,
} from './reader-rows';
import { TriState, chapterDialogTotals } from './selection';
import { SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';
import { buildRoster } from './speaker-roster';
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
 * Ticket 11 adds the node menus on every row and the book-level actions in the toolbar overflow
 * (titles, pauses, reread, manual reread); all of them post through {@link BookEditor}. Ticket 12
 * adds the paragraph selection (row and tree checkboxes, "Select unprocessed") and the selection
 * action bar — Attribute, Bulk assign speaker, Clear — over the {@link SelectionStore}.
 */
@Component({
  selector: 'app-book-page',
  imports: [
    ScrollingModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDividerModule,
    MatIconModule,
    MatMenuModule,
    MatTooltipModule,
    RouterLink,
    EmptyState,
    MeasuredScrollDirective,
    NodeMenu,
    ParagraphRow,
    SpeakerMenu,
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

      @if (selectable() && selection.count() > 0) {
        <div class="book__selection" role="group" aria-label="Selection" data-testid="selection-bar">
          <span class="book__selection-count" data-testid="selection-count">
            {{ selection.count() }} paragraph{{ selection.count() === 1 ? '' : 's' }}
          </span>
          <button
            mat-flat-button
            type="button"
            data-action="attribute-selection"
            [disabled]="working() || editor.locked()"
            (click)="attributeSelection()"
          >
            <mat-icon>auto_awesome</mat-icon>
            Attribute
          </button>
          <button
            mat-stroked-button
            type="button"
            data-action="bulk-assign"
            [matMenuTriggerFor]="bulk"
            [disabled]="working() || editor.locked() || queueBusy()"
            [matTooltip]="queueBusy() ? 'Bulk assign waits until attribution has finished' : ''"
          >
            <mat-icon>group</mat-icon>
            Bulk assign speaker
            <mat-icon iconPositionEnd>arrow_drop_down</mat-icon>
          </button>
          <mat-menu #bulk="matMenu" class="book__bulk-menu">
            <r2m-speaker-menu
              [roster]="roster()"
              (pick)="bulkAssign($event)"
              (clear)="bulkAssign(null)"
              (create)="bulkCreate($event)"
            />
          </mat-menu>
          <button mat-button type="button" data-action="clear-selection" (click)="selection.clear()">
            Clear
          </button>
        </div>
      }

      <span class="book__spacer"></span>

      @if (!noContent()) {
        <button
          mat-icon-button
          type="button"
          aria-label="Book actions"
          data-testid="book-actions"
          [matMenuTriggerFor]="actions"
          [disabled]="editor.locked()"
        >
          <mat-icon>more_vert</mat-icon>
        </button>
        <mat-menu #actions="matMenu">
          <button mat-menu-item type="button" data-action="add-book-title" (click)="run({ type: 'AddBookTitle' })">
            <mat-icon>title</mat-icon><span>Add book title</span>
          </button>
          @if (titleActions().volumes) {
            <button mat-menu-item type="button" data-action="add-volume-titles" (click)="run({ type: 'AddVolumeTitles' })">
              <mat-icon>library_books</mat-icon><span>Add volume titles</span>
            </button>
          }
          @if (titleActions().parts) {
            <button mat-menu-item type="button" data-action="add-part-titles" (click)="run({ type: 'AddPartTitles' })">
              <mat-icon>bookmark</mat-icon><span>Add part titles</span>
            </button>
          }
          @if (titleActions().chapters) {
            <button mat-menu-item type="button" data-action="add-chapter-titles" (click)="run({ type: 'AddChapterTitles' })">
              <mat-icon>menu_book</mat-icon><span>Add chapter titles</span>
            </button>
          }
          <mat-divider></mat-divider>
          <button mat-menu-item type="button" data-action="add-pauses" (click)="run({ type: 'AddPauses' })">
            <mat-icon>pause</mat-icon><span>Add pauses</span>
          </button>
          <mat-divider></mat-divider>
          <button mat-menu-item type="button" data-action="reread" (click)="reread()">
            <mat-icon>restart_alt</mat-icon><span>Reread…</span>
          </button>
          <button mat-menu-item type="button" data-action="manual-reread" (click)="rereadManually()">
            <mat-icon>tune</mat-icon><span>Manual reread…</span>
          </button>
        </mat-menu>
      }
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
              [selectable]="selectable()"
              [nodeStates]="nodeStates()"
              (expandedChange)="store.setExpanded($event.node, $event.expanded)"
              (selectChapter)="openChapter($event)"
              (toggleNode)="toggleNode($event.node, $event.on)"
              (selectUnprocessed)="selectUnprocessed($event)"
              (attributeNode)="attributeNode($event)"
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
                  <r2m-paragraph
                    [paragraph]="row.paragraph"
                    [chapterId]="row.chapterId"
                    [ctx]="ctx()"
                    [isFirst]="row.isFirst"
                    [isLast]="row.isLast"
                  />
                }
                @case ('pause') {
                  <div class="book__pause" role="separator" [attr.aria-label]="row.label">
                    <span class="book__pause-label">{{ row.label }}</span>
                    <span class="book__pause-rule"></span>
                    <r2m-node-menu class="book__pause-menu" [target]="pauseTarget(row)" />
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
    .book__spacer {
      flex: 1 1 auto;
    }
    .book__selection {
      display: flex;
      align-items: center;
      gap: var(--r2m-space-2);
      padding: 2px var(--r2m-space-2) 2px var(--r2m-space-3);
      border-radius: var(--r2m-radius-pill);
      background: color-mix(in srgb, var(--r2m-accent) 10%, transparent);
    }
    .book__selection-count {
      font-weight: 600;
      white-space: nowrap;
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
    .book__pause-rule {
      flex: 1 1 auto;
      border-top: 1px dashed var(--r2m-outline);
    }
    .book__pause-menu {
      opacity: 0;
      transition: opacity 120ms;
    }
    .book__pause:hover .book__pause-menu,
    .book__pause:focus-within .book__pause-menu {
      opacity: 1;
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
  protected readonly editor = inject(BookEditor);
  protected readonly selection = inject(SelectionStore);
  private readonly assigner = inject(SpeakerAssigner);
  private readonly attribution = inject(AttributionApi);
  private readonly book = inject(BookApi);
  private readonly live = inject(LiveService);
  private readonly preflight = inject(Preflight);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly dialog = inject(MatDialog);
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

  /** Which "Add … titles" entries apply (research §3 Toolbar): only levels that add navigation. */
  protected readonly titleActions = computed(() => {
    const overview = this.store.overview();
    const volumes = overview?.volumes.length ?? 0;
    const parts = overview?.totalParts ?? 0;
    const chapters = overview?.totalChapters ?? 0;
    return { volumes: volumes > 1, parts: parts > volumes, chapters: chapters > parts };
  });
  protected readonly keys = computed(() => this.rows().map((r) => r.key));

  /** Paragraph selection lives in Read and Speakers modes; Audio selects items (ticket 13). */
  protected readonly selectable = computed(() => this.store.mode() !== 'audio');
  /** A selection read or enqueue in flight: the action bar waits for it. */
  protected readonly working = signal(false);
  /** Bulk assign is disarmed while attribution runs anywhere (research §3). */
  protected readonly queueBusy = computed(() => this.live.queue()?.attribution.isBusy ?? false);

  protected readonly roster = computed(() =>
    buildRoster(this.store.overview()?.characters ?? [], this.project.detail()?.narrator ?? null),
  );

  /** Every tree node's checkbox state over the current selection. */
  protected readonly nodeStates = computed(() => {
    const states: Record<string, TriState> = {};
    const walk = (nodes: readonly TreeNode[]) => {
      for (const node of nodes) {
        states[node.id] = this.selection.nodeState(node.level, node.id);
        walk(node.children);
      }
    };
    walk(this.store.tree());
    return states;
  });

  protected readonly ctx = computed<RowContext>(() => ({
    folder: this.store.folder() ?? '',
    mode: this.store.mode(),
    speakers: this.store.speakers(),
    paragraphStatus: this.project.paragraphs(),
    itemStatus: this.project.items(),
    voices: this.store.itemVoices(),
    reviews: this.store.reviews(),
    selectable: this.selectable(),
    selected: this.selection.selected(),
    ancestry: this.store.ancestry(),
    roster: this.roster(),
  }));

  protected readonly trackRow = (_: number, row: ReaderRow) => row.key;

  constructor() {
    effect(() => {
      const mode = parseMode(this.mode());
      untracked(() => this.store.setMode(mode));
    });

    // A loaded chapter tells the selection how many Character paragraphs it holds.
    effect(() => {
      const totals = chapterDialogTotals(this.store.chapters());
      untracked(() => this.selection.learnTotals(totals));
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

  protected pauseTarget(row: Extract<ReaderRow, { kind: 'pause' }>): NodeMenuTarget {
    return {
      kind: 'pause-paragraph',
      id: row.paragraph.id,
      text: row.label,
      isFirst: row.isFirst,
      isLast: row.isLast,
    };
  }

  protected run(command: BookCommand): void {
    void this.editor.run(command);
  }

  // ---- selection (ticket 12) --------------------------------------------------------------------

  /** A tree checkbox: every Character paragraph under the node joins or leaves the selection. */
  protected async toggleNode(node: TreeNode, on: boolean): Promise<void> {
    await this.withSelectionRead(async (folder) => {
      const refs = await this.book.paragraphIds(folder, node.level, node.id);
      // A full read is the node's total, whichever way the box went.
      this.selection.learnTotals({ [node.id]: refs.length });
      if (on) this.selection.add(refs);
      else this.selection.remove(refs.map((r) => r.id));
    });
  }

  /** "Select unprocessed": what Blazor selects, without loading every chapter. */
  protected async selectUnprocessed(node: TreeNode): Promise<void> {
    await this.withSelectionRead(async (folder) => {
      this.selection.add(await this.book.paragraphIds(folder, node.level, node.id, true));
    });
  }

  /** "Attribute unprocessed" on a node: the node-scoped enqueue the overview also uses. */
  protected async attributeNode(node: TreeNode): Promise<void> {
    await this.enqueue((folder) =>
      this.attribution.enqueue(folder, { level: node.level, nodeId: node.id, unprocessedOnly: true }),
    );
  }

  /** The action bar's Attribute: queue the selection, then let it go (Blazor clears too). */
  protected async attributeSelection(): Promise<void> {
    const ids = this.selection.ids();
    if (ids.length === 0) return;
    const queued = await this.enqueue((folder) => this.attribution.enqueueParagraphs(folder, ids));
    if (queued) this.selection.clear();
  }

  protected bulkAssign(characterId: string | null): void {
    void this.assigner.assign(
      { kind: 'selection', paragraphIds: this.selection.ids() },
      characterId,
    );
  }

  protected bulkCreate(name: string): void {
    void this.assigner.createAndAssign(
      { kind: 'selection', paragraphIds: this.selection.ids() },
      name,
    );
  }

  /** Preflight, then one enqueue; the toast says how many. True when something was queued. */
  private async enqueue(
    request: (folder: string) => Promise<{ enqueued: number }>,
  ): Promise<boolean> {
    const folder = this.store.folder();
    if (!folder || this.working()) return false;
    this.working.set(true);
    try {
      if (!(await this.preflight.ensureReady('attribution'))) return false;
      const { enqueued } = await request(folder);
      this.toast.success(
        enqueued === 0 ? 'Nothing to queue' : `Queued ${enqueued} paragraph${enqueued === 1 ? '' : 's'}`,
      );
      return enqueued > 0;
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    } finally {
      this.working.set(false);
    }
  }

  private async withSelectionRead(read: (folder: string) => Promise<void>): Promise<void> {
    const folder = this.store.folder();
    if (!folder) return;
    this.working.set(true);
    try {
      await read(folder);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.working.set(false);
    }
  }

  protected async reread(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Reread the book?',
      message:
        'This deletes all book data — attribution, generated audio, and edits — and ' +
        'reprocesses the source file from scratch. This cannot be undone.',
      destructive: true,
      confirmLabel: 'Delete and reread',
    });
    if (ok) await this.editor.reread();
  }

  protected async rereadManually(): Promise<void> {
    const ref = this.dialog.open<ManualRereadDialog, void, ManualImportRequest>(ManualRereadDialog, {
      autoFocus: 'first-tabbable',
      restoreFocus: true,
    });
    const request = await firstValueFrom(ref.afterClosed());
    if (request) await this.editor.rereadManually(request);
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
