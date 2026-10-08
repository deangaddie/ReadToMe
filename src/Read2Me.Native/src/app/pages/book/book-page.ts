import { html, nothing } from 'lit-html';
import { AttributionApi, BookApi, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { LiveService } from '@app/live/live.service';
import { Preflight } from '@app/shared/preflight';
import type { MeasuredList } from '@app/ui/measured-list';
import { emptyState, icon } from '@app/ui/partials';
import type { SpeakerMenu } from '@app/ui/speaker-menu';
import { ToastService } from '@app/ui/toast';
import { ProjectStore } from '../project/project-store';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import type { TreeNode } from './book-tree';
import { nodeMenuTrigger } from './node-menu';
import type { ActionEntryId, NodeMenuTarget, SelectionKind } from './node-menu-entries';
import { paragraphRow } from './paragraph-row';
import {
  READER_MODES,
  type ReaderMode,
  type ReaderRow,
  type RowContext,
  buildRows,
  parseMode,
} from './reader-rows';
import { type TriState, chapterDialogTotals, chapterVoicedTotals } from './selection';
import { AudioSelectionStore, SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';
import { buildRoster } from './speaker-roster';
import '@app/ui/measured-list';
import '@app/ui/speaker-menu';
import './node-menu';
import './structure-tree';
import bookCss from './book.css' with { type: 'text' };

adoptStyles(bookCss);

/** Load the adjacent chapter when the viewport is this close to either end. */
const EDGE_PX = 600;
/** How often the viewport edges are checked; see {@link BookPage.checkEdges}. */
const EDGE_POLL_MS = 250;

const MODE_LABELS: Record<ReaderMode, string> = {
  read: 'Read',
  speakers: 'Speakers',
  audio: 'Audio',
};

const SKELETON = [1, 2, 3, 4, 5, 6];
const BULK_MENU_ID = 'r2m-book-bulk-menu';

/**
 * `/projects/{folder}/book` (design §6.3, native-web 22 + 23 + 25): the structure tree beside a
 * virtual-scrolled window of adjacent chapters in `<r2m-measured-list>`. The mode (`?mode=`)
 * changes what each row shows, never where it is; `?chapter=` opens at a chapter. Rows are
 * partials rendered from the list's one effect: they read {@link ctx} there, so a live status
 * delta re-renders the visible rows and nothing else. The current chapter follows the list's top
 * row (`data-current-chapter`) and the tree, and the book reloads from live receipts through the
 * {@link BookStore}. One shared `<r2m-node-menu>` serves every tree node and row trigger.
 *
 * The paragraph selection (ticket 12) lives in Read and Speakers modes: row and tree checkboxes
 * and the tree's "Select unprocessed" over the {@link SelectionStore}, and the selection action
 * bar — Attribute (gated by preflight), Bulk assign speaker, Clear. Audio mode selects items
 * (ticket 13) through the row checkboxes over the {@link AudioSelectionStore}; its bar, tree
 * checkboxes and the generator arrive with native-web 26, the book actions with 24–27.
 */
export class BookPage extends R2mElement {
  private readonly router = use(Router);
  private readonly book = use(BookApi);
  private readonly attribution = use(AttributionApi);
  private readonly live = use(LiveService);
  private readonly preflight = use(Preflight);
  private readonly toast = use(ToastService);
  private project!: ProjectStore;
  private store!: BookStore;
  private editor!: BookEditor;
  private selection!: SelectionStore;
  private audioSelection!: AudioSelectionStore;
  private assigner!: SpeakerAssigner;
  private readonly treeOpen = signal(true);
  /** A selection read or enqueue in flight: the action bar waits for it. */
  private readonly working = signal(false);
  /** The bulk-assign menu is open: its speaker menu is rendered fresh per open. */
  private readonly bulkOpen = signal(false);

  /** The chapter last opened from a link or the tree, until the viewport has scrolled it to the top. */
  private scrollTarget: string | null = null;
  private rows = computed<ReaderRow[]>(() => []);
  private ctx = computed<RowContext>(() => EMPTY_CONTEXT);
  private noContent = computed(() => false);
  private roster = computed(() => buildRoster([], null));
  /** Paragraph selection lives in Read and Speakers modes; Audio selects items (ticket 13). */
  private selectable = computed(() => true);
  private audioSelectable = computed(() => false);
  /** Which selection the tree offers checkboxes and shortcuts for; null for none. */
  private treeSelection = computed<SelectionKind | null>(() => null);
  private nodeStates = computed<Readonly<Record<string, TriState>>>(() => ({}));
  /** Bulk assign is disarmed while attribution runs anywhere (research §3). */
  private queueBusy = computed(() => false);

  protected override connected(): void {
    this.project = use(ProjectStore, this);
    this.store = use(BookStore, this);
    this.editor = use(BookEditor, this);
    this.selection = use(SelectionStore, this);
    this.audioSelection = use(AudioSelectionStore, this);
    this.assigner = use(SpeakerAssigner, this);
    const { project, store, editor, selection, audioSelection } = this;

    this.noContent = computed(() => store.overview()?.hasContent === false);
    this.rows = computed(() => buildRows(store.chapters(), store.mode()));
    this.roster = computed(() =>
      buildRoster(store.overview()?.characters ?? [], project.detail()?.narrator ?? null),
    );
    this.selectable = computed(() => store.mode() !== 'audio');
    this.audioSelectable = computed(() => store.mode() === 'audio');
    // The item selection's tree checkboxes and shortcuts arrive with native-web 26.
    this.treeSelection = computed(() => (this.selectable() ? 'paragraphs' : null));
    this.queueBusy = computed(() => this.live.queue()?.attribution.isBusy ?? false);
    const narratorOnlyMode = computed(() => project.detail()?.narratorOnlyMode ?? false);

    // Every tree node's checkbox state over the paragraph selection.
    this.nodeStates = computed(() => {
      const states: Record<string, TriState> = {};
      const walk = (nodes: readonly TreeNode[]) => {
        for (const node of nodes) {
          states[node.id] = selection.nodeState(node.level, node.id);
          walk(node.children);
        }
      };
      walk(store.tree());
      return states;
    });

    this.ctx = computed<RowContext>(() => ({
      folder: store.folder() ?? '',
      mode: store.mode(),
      speakers: store.speakers(),
      paragraphStatus: project.paragraphs(),
      itemStatus: project.items(),
      voices: store.itemVoices(),
      reviews: store.reviews(),
      selectable: this.selectable(),
      selected: selection.selected(),
      itemSelectable: this.audioSelectable(),
      selectedItems: audioSelection.selected(),
      narratorOnlyMode: narratorOnlyMode(),
      ancestry: store.ancestry(),
      roster: this.roster(),
      locked: editor.locked(),
    }));

    this.effect(() => {
      const mode = parseMode(this.router.query().get('mode'));
      untracked(() => store.setMode(mode));
    });

    // A loaded chapter tells each selection how many rows of its kind it holds.
    this.effect(() => {
      const totals = chapterDialogTotals(store.chapters());
      untracked(() => selection.learnTotals(totals));
    });
    this.effect(() => {
      const totals = chapterVoicedTotals(store.chapters(), narratorOnlyMode());
      untracked(() => audioSelection.learnTotals(totals));
    });

    this.effect(() => {
      const folder = project.folder();
      if (!folder) return;
      untracked(() => void this.start(folder));
    });

    this.effect(() => {
      const request = store.scrollRequest();
      if (!request) return;
      untracked(() => this.scrollToChapter(request.chapterId));
    });

    // The tree (native-web 23) reads the store; E2E reads this attribute.
    this.effect(() => {
      const current = store.currentChapterId();
      if (current) this.setAttribute('data-current-chapter', current);
      else this.removeAttribute('data-current-chapter');
    });

    const edges = setInterval(() => this.checkEdges(), EDGE_POLL_MS);
    this.onDisconnect(() => clearInterval(edges));
  }

  protected template() {
    const store = this.store;
    const rows = this.rows();
    const stale = store.stale();
    const treeOpen = this.treeOpen();
    return html`
      <div class="book__toolbar" role="toolbar" aria-label="Reader">
        <button
          type="button"
          class="r2m-icon-button"
          aria-label=${treeOpen ? 'Hide structure' : 'Show structure'}
          aria-expanded=${treeOpen ? 'true' : 'false'}
          @click=${() => this.treeOpen.set(!treeOpen)}
        >
          ${icon(treeOpen ? 'left_panel_close' : 'left_panel_open')}
        </button>
        <fieldset class="r2m-segmented" role="radiogroup" aria-label="Reader mode">
          ${READER_MODES.map(
            (m) =>
              html`<label>
                <input
                  type="radio"
                  name="reader-mode"
                  .value=${m}
                  .checked=${store.mode() === m}
                  @change=${() => this.setMode(m)}
                />
                ${MODE_LABELS[m]}
              </label>`,
          )}
        </fieldset>
        ${this.selectable() && this.selection.count() > 0 ? this.selectionBar() : nothing}
        <span class="book__spacer"></span>
      </div>

      ${
        stale
          ? html`<div class="book__stale" role="alert">
              ${icon('sync_problem')}
              <span>This view may be out of date: ${stale}</span>
              <button type="button" class="r2m-button" @click=${this.refresh}>Refresh</button>
            </div>`
          : nothing
      }
      ${
        this.noContent()
          ? emptyState(
              {
                icon: 'menu_book',
                headline: 'The book has not been read in yet',
                hint: 'Read it in from the project overview.',
              },
              html`<a class="r2m-button r2m-button--filled" href="./">Go to overview</a>`,
            )
          : html`<div class="book__body ${treeOpen ? '' : 'book__body--tree-hidden'}">
              ${
                treeOpen
                  ? html`<nav class="book__tree" aria-label="Structure">
                      <r2m-structure-tree
                        .nodes=${store.tree()}
                        .statuses=${this.project.nodes()}
                        .currentChapterId=${store.currentChapterId()}
                        .expandedIds=${store.expanded()}
                        .selection=${this.treeSelection()}
                        .nodeStates=${this.nodeStates()}
                        .locked=${this.editor.locked()}
                        @expanded-change=${this.onExpandedChange}
                        @select-chapter=${this.onSelectChapter}
                        @toggle-node=${this.onToggleNode}
                        @node-action=${this.onNodeAction}
                      ></r2m-structure-tree>
                    </nav>`
                  : nothing
              }
              <section class="book__reader" aria-label="Chapter">
                ${
                  rows.length === 0
                    ? html`<div
                        class="book__skeleton"
                        aria-busy="true"
                        aria-label="Loading chapter"
                      >
                        ${SKELETON.map(() => html`<div class="book__skeleton-line"></div>`)}
                      </div>`
                    : nothing
                }
                <r2m-measured-list
                  class="book__viewport ${rows.length === 0 ? 'book__viewport--empty' : ''}"
                  .key=${rowKey}
                  .row=${this.renderRow}
                  .items=${rows}
                  @r2m-top-row=${this.onTopRow}
                ></r2m-measured-list>
              </section>
            </div>`
      }
      <r2m-node-menu></r2m-node-menu>
    `;
  }

  /** The paragraph selection's action bar (ticket 12): count, Attribute, Bulk assign speaker, Clear. */
  private selectionBar() {
    const count = this.selection.count();
    const busy = this.working() || this.editor.locked();
    const queueBusy = this.queueBusy();
    return html`<div
      class="book__selection"
      role="group"
      aria-label="Selection"
      data-testid="selection-bar"
    >
      <span class="book__selection-count" data-testid="selection-count"
        >${count} paragraph${count === 1 ? '' : 's'}</span
      >
      <button
        type="button"
        class="r2m-button r2m-button--filled"
        data-action="attribute-selection"
        ?disabled=${busy}
        @click=${this.attributeSelection}
      >
        ${icon('auto_awesome')} Attribute
      </button>
      <span class="book__bulk">
        <button
          type="button"
          class="r2m-button r2m-button--stroked"
          data-action="bulk-assign"
          popovertarget=${BULK_MENU_ID}
          aria-haspopup="listbox"
          ?disabled=${busy || queueBusy}
          data-tooltip=${queueBusy ? 'Bulk assign waits until attribution has finished' : nothing}
        >
          ${icon('groups')} Bulk assign speaker ${icon('expand_more')}
        </button>
        <div
          id=${BULK_MENU_ID}
          popover
          class="r2m-menu book__bulk-menu"
          @toggle=${this.onBulkToggle}
          @pick=${(e: Event) => this.bulkAssign((e as CustomEvent<string>).detail)}
          @clear=${() => this.bulkAssign(null)}
          @create=${(e: Event) => this.bulkCreate((e as CustomEvent<string>).detail)}
        >
          ${
            this.bulkOpen()
              ? html`<r2m-speaker-menu .roster=${this.roster()}></r2m-speaker-menu>`
              : nothing
          }
        </div>
      </span>
      <button
        type="button"
        class="r2m-button"
        data-action="clear-selection"
        @click=${() => this.selection.clear()}
      >
        Clear
      </button>
    </div>`;
  }

  private readonly onExpandedChange = (e: Event): void => {
    const { node, expanded } = (e as CustomEvent<{ node: TreeNode; expanded: boolean }>).detail;
    this.store.setExpanded(node, expanded);
  };

  /** A tree pick opens the chapter and names it in the URL, so a reload or a shared link keeps it. */
  private readonly onSelectChapter = (e: Event): void => {
    const chapterId = (e as CustomEvent<string>).detail;
    void this.store.openChapter(chapterId);
    const query = Object.fromEntries(this.router.query());
    this.router.navigate(this.router.path(), {
      replace: true,
      query: { ...query, chapter: chapterId },
    });
  };

  /** Reads {@link ctx} inside the list's effect, so status deltas re-render only the rows. */
  private readonly renderRow = (row: ReaderRow) => {
    switch (row.kind) {
      case 'chapter':
        return html`<h2 class="book__chapter book__row">${row.title}</h2>`;
      case 'paragraph':
        return html`<div class="book__row">
          ${paragraphRow(row.paragraph, row.chapterId, this.ctx(), row)}
        </div>`;
      case 'pause': {
        const target: NodeMenuTarget = {
          kind: 'pause-paragraph',
          id: row.paragraph.id,
          text: row.label,
          isFirst: row.isFirst,
          isLast: row.isLast,
        };
        return html`<div class="book__pause book__row" role="separator" aria-label=${row.label}>
          <span class="book__pause-label">${row.label}</span>
          <span class="book__pause-rule"></span>
          <span class="book__pause-menu"
            >${nodeMenuTrigger(target, { disabled: this.ctx().locked })}</span
          >
        </div>`;
      }
    }
  };

  private setMode(mode: ReaderMode): void {
    this.store.setMode(mode);
    const query = Object.fromEntries(this.router.query());
    this.router.navigate(this.router.path(), {
      replace: true,
      query: { ...query, mode: mode === 'read' ? null : mode },
    });
  }

  private readonly onTopRow = (e: Event): void => {
    const row = this.rows()[(e as CustomEvent<number>).detail];
    if (row) this.store.setCurrentChapter(row.chapterId);
  };

  private readonly refresh = (): void => {
    void this.store.refresh();
  };

  // ---- selection (ticket 12) --------------------------------------------------------------------

  /** A tree checkbox: every Character paragraph under the node joins or leaves the selection. */
  private readonly onToggleNode = (e: Event): void => {
    const { node, on } = (e as CustomEvent<{ node: TreeNode; on: boolean }>).detail;
    void this.withSelectionRead(async (folder) => {
      const refs = await this.book.paragraphIds(folder, node.level, node.id);
      // A full read is the node's total, whichever way the box went.
      this.selection.learnTotals({ [node.id]: refs.length });
      if (on) this.selection.add(refs);
      else this.selection.remove(refs.map((r) => r.id));
    });
  };

  /** The node menu's selection shortcuts; the audio ones arrive with native-web 26. */
  private readonly onNodeAction = (e: Event): void => {
    const { node, action } = (e as CustomEvent<{ node: TreeNode; action: ActionEntryId }>).detail;
    switch (action) {
      case 'select-unprocessed':
        // The node's unprocessed paragraphs, without loading every chapter.
        void this.withSelectionRead(async (folder) => {
          this.selection.add(await this.book.paragraphIds(folder, node.level, node.id, true));
        });
        return;
      case 'attribute-node':
        // The node-scoped enqueue the overview also uses.
        void this.enqueue((folder) =>
          this.attribution.enqueue(folder, {
            level: node.level,
            nodeId: node.id,
            unprocessedOnly: true,
          }),
        );
        return;
      default:
        return;
    }
  };

  /** The action bar's Attribute: queue the selection, then clear it: the queue owns it now. */
  private readonly attributeSelection = async (): Promise<void> => {
    const ids = this.selection.ids();
    if (ids.length === 0) return;
    const queued = await this.enqueue((folder) => this.attribution.enqueueParagraphs(folder, ids));
    if (queued) this.selection.clear();
  };

  /** A pick, clear or create closes the menu, like a mat-menu item click. */
  private readonly onBulkToggle = (e: ToggleEvent): void => {
    const open = e.newState === 'open';
    this.bulkOpen.set(open);
    // Mouse or trackpad: type to search straight away, as the chip's menu does. Touch skips it.
    if (open && matchMedia('(pointer: fine)').matches) {
      void this.rendered().then(() =>
        this.querySelector<SpeakerMenu>('.book__bulk-menu r2m-speaker-menu')?.focusSearch(),
      );
    }
  };

  private closeBulkMenu(): void {
    const menu = this.querySelector<HTMLElement>('.book__bulk-menu');
    if (menu?.matches(':popover-open')) menu.hidePopover?.();
  }

  private bulkAssign(characterId: string | null): void {
    this.closeBulkMenu();
    void this.assigner.assign(
      { kind: 'selection', paragraphIds: this.selection.ids() },
      characterId,
    );
  }

  private bulkCreate(name: string): void {
    this.closeBulkMenu();
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
        enqueued === 0
          ? 'Nothing to queue'
          : `Queued ${enqueued} paragraph${enqueued === 1 ? '' : 's'}`,
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

  // ---- the window ---------------------------------------------------------------------------------

  private list(): MeasuredList<ReaderRow> | null {
    return this.querySelector<MeasuredList<ReaderRow>>('r2m-measured-list');
  }

  private async start(folder: string): Promise<void> {
    await this.store.open(folder);
    if (this.noContent()) return;
    // `?chapter=` wins over whatever the window holds: a reload may have opened the first chapter
    // while the overview was still loading.
    const chapter = this.router.query().get('chapter');
    if (chapter && !this.store.window().includes(chapter)) await this.store.openChapter(chapter);
    else if (this.store.window().length === 0) await this.store.openFirstChapter();
  }

  private scrollToChapter(chapterId: string): void {
    void this.rendered().then(() => {
      const index = this.chapterRowIndex(chapterId);
      const list = this.list();
      if (index < 0 || !list) return;
      this.scrollTarget = chapterId;
      list.scrollToIndex(index);
      // The chapter before is needed to scroll up from the top of this one; the edge check loads
      // it once that can keep this chapter in place.
      this.checkEdges();
    });
  }

  /**
   * Loads the neighbouring chapter when the viewport nears an end. Polled rather than tied to
   * scroll events: a chapter shorter than the viewport never scrolls, and wheel input at the very
   * top fires no scroll event at all.
   *
   * Two rules keep the chapter the reader is on in place when chapters are short (a part whose
   * only chapter is its heading). Content shorter than the viewport cannot scroll, so a chapter
   * prepended to it pushes the reader's chapter down: load next until there is something to
   * scroll, and previous only then (or when there is no next). And a full window drops its far
   * end only when that chapter is off screen by a margin; dropping one still in view would empty
   * the screen and start the loads over. A chapter opened from a link that could not scroll to
   * the top yet ({@link scrollTarget}) is scrolled there once the chapters after it have loaded.
   */
  private checkEdges(): void {
    const list = this.list();
    if (!list || this.rows().length === 0) return;
    const top = list.scrollOffset('top');
    const bottom = list.scrollOffset('bottom');
    const size = list.clientHeight;
    const loaded = this.store.window();
    const scrollable = top + bottom >= 1;

    const reaching = this.reachScrollTarget(list, top, bottom);
    if (bottom < EDGE_PX) {
      // Dropping the first chapter is safe when the second starts well above the viewport.
      const second = loaded[1];
      void this.store.loadNext(second !== undefined && this.chapterOffset(second) <= top - EDGE_PX);
    }
    if (!reaching && top < EDGE_PX && (scrollable || this.store.atEnd())) {
      const last = loaded.at(-1);
      void this.store.loadPrevious(
        last !== undefined && this.chapterOffset(last) >= top + size + EDGE_PX,
      );
    }
  }

  /**
   * Scrolls on to {@link scrollTarget} when the content below it has grown; true while the target
   * is still out of reach. It is given up once reached, gone, or the book has nothing more to load.
   */
  private reachScrollTarget(list: MeasuredList<ReaderRow>, top: number, bottom: number): boolean {
    const chapterId = this.scrollTarget;
    if (!chapterId) return false;
    const target = this.chapterOffset(chapterId);
    if (!Number.isFinite(target) || top >= target - 1 || this.store.atEnd()) {
      this.scrollTarget = null;
      return false;
    }
    if (bottom >= 1) list.scrollToOffset(target);
    return true;
  }

  /** Where a loaded chapter's header row starts; a chapter with no rows sits off screen. */
  private chapterOffset(chapterId: string): number {
    const index = this.chapterRowIndex(chapterId);
    const list = this.list();
    return index < 0 || !list ? Number.POSITIVE_INFINITY : list.offsetOf(index);
  }

  /** The row index of a chapter's header, -1 when the chapter is not in the window. */
  private chapterRowIndex(chapterId: string): number {
    return this.rows().findIndex((r) => r.kind === 'chapter' && r.chapterId === chapterId);
  }
}

const rowKey = (row: ReaderRow) => row.key;

const EMPTY_CONTEXT: RowContext = {
  folder: '',
  mode: 'read',
  speakers: { names: {}, narrator: null },
  paragraphStatus: {},
  itemStatus: {},
  voices: {},
  reviews: {},
  selectable: false,
  selected: new Set(),
  itemSelectable: false,
  selectedItems: new Set(),
  narratorOnlyMode: false,
  ancestry: {},
  roster: [],
  locked: false,
};

define('r2m-book-page', BookPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-book-page': BookPage;
  }
}
