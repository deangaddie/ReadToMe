import { html, nothing } from 'lit-html';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { computed, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import type { MeasuredList } from '@app/ui/measured-list';
import { emptyState, icon } from '@app/ui/partials';
import { ProjectStore } from '../project/project-store';
import { BookStore } from './book-store';
import { paragraphRow } from './paragraph-row';
import {
  READER_MODES,
  type ReaderMode,
  type ReaderRow,
  type RowContext,
  buildRows,
  parseMode,
} from './reader-rows';
import { buildRoster } from './speaker-roster';
import '@app/ui/measured-list';
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

/**
 * `/projects/{folder}/book` (design §6.3, native-web 22): a virtual-scrolled window of adjacent
 * chapters in `<r2m-measured-list>`. The mode (`?mode=`) changes what each row shows, never where
 * it is; `?chapter=` opens at a chapter. Rows are partials rendered from the list's one effect:
 * they read {@link ctx} there, so a live status delta re-renders the visible rows and nothing
 * else. The current chapter follows the list's top row (`data-current-chapter`), and the book
 * reloads from live receipts through the {@link BookStore}. The structure tree, node menus, book
 * actions, selection and the audio actions arrive with native-web 23–27.
 */
export class BookPage extends R2mElement {
  private readonly router = use(Router);
  private project!: ProjectStore;
  private store!: BookStore;

  /** The chapter last opened from a link or the tree, until the viewport has scrolled it to the top. */
  private scrollTarget: string | null = null;
  private rows = computed<ReaderRow[]>(() => []);
  private ctx = computed<RowContext>(() => EMPTY_CONTEXT);
  private noContent = computed(() => false);

  protected override connected(): void {
    this.project = use(ProjectStore, this);
    this.store = use(BookStore, this);
    const { project, store } = this;

    this.noContent = computed(() => store.overview()?.hasContent === false);
    this.rows = computed(() => buildRows(store.chapters(), store.mode()));
    const roster = computed(() =>
      buildRoster(store.overview()?.characters ?? [], project.detail()?.narrator ?? null),
    );
    // Selection is native-web 25: until then the rows render with nothing selected.
    const none = new Set<string>();
    this.ctx = computed<RowContext>(() => ({
      folder: store.folder() ?? '',
      mode: store.mode(),
      speakers: store.speakers(),
      paragraphStatus: project.paragraphs(),
      itemStatus: project.items(),
      voices: store.itemVoices(),
      reviews: store.reviews(),
      selectable: false,
      selected: none,
      itemSelectable: false,
      selectedItems: none,
      narratorOnlyMode: project.detail()?.narratorOnlyMode ?? false,
      ancestry: store.ancestry(),
      roster: roster(),
    }));

    this.effect(() => {
      const mode = parseMode(this.router.query().get('mode'));
      untracked(() => store.setMode(mode));
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
    return html`
      <div class="book__toolbar" role="toolbar" aria-label="Reader">
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
          : html`<div class="book__body">
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
    `;
  }

  /** Reads {@link ctx} inside the list's effect, so status deltas re-render only the rows. */
  private readonly renderRow = (row: ReaderRow) => {
    switch (row.kind) {
      case 'chapter':
        return html`<h2 class="book__chapter book__row">${row.title}</h2>`;
      case 'paragraph':
        return html`<div class="book__row">
          ${paragraphRow(row.paragraph, row.chapterId, this.ctx(), row)}
        </div>`;
      case 'pause':
        return html`<div class="book__pause book__row" role="separator" aria-label=${row.label}>
          <span class="book__pause-label">${row.label}</span>
          <span class="book__pause-rule"></span>
        </div>`;
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
};

define('r2m-book-page', BookPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-book-page': BookPage;
  }
}
