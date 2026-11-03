import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { type ProjectSummary, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { Router } from '@app/core/router';
import { use } from '@app/core/services';
import { signal, untracked } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { ConfirmService } from '@app/ui/dialogs';
import { menuItem } from '@app/ui/menu';
import { emptyState, icon, statusChip } from '@app/ui/partials';
import { projectCard } from '@app/ui/project-card';
import { ToastService } from '@app/ui/toast';
import { openNewProjectDialog } from './new-project-dialog';
import {
  type DraftRow,
  draftPositions,
  draftRows,
  positionsProblem,
  rebaseDraft,
  saveBlockedReason,
} from './positions-draft';
import { ProjectsStore } from './projects-store';
import { SeriesActions } from './series-actions';
import { type SeriesPanelTab, seriesCard, seriesPanel } from './series-card';
import './series-cast-board';
import { deleteProjectConfirm } from './permanent-changes';
import {
  bookLabel,
  type Membership,
  panelAfter,
  type SeriesMember,
  type SeriesView,
} from './series-shelf';
import projectsCss from './projects-page.css' with { type: 'text' };

adoptStyles(projectsCss);

/** Skeleton cards drawn while the first load is in flight. */
const SKELETONS = [0, 1, 2, 3, 4, 5];

export type ProjectsView = 'books' | 'series';

/** What every book card reads, taken once per render. */
interface CardState {
  /** Folder of the project whose delete is in flight. */
  deleting: string | null;
  membership: ReadonlyMap<string, Membership>;
}

/** The Books / Series toggle, remembered per browser (#207, 1). */
const VIEW_KEY = 'r2m.projects.view';

function readView(): ProjectsView {
  try {
    return localStorage.getItem(VIEW_KEY) === 'series' ? 'series' : 'books';
  } catch {
    return 'books';
  }
}

/**
 * `/projects` (design §6.1): the shelf over {@link ProjectsStore}. The Books view is the grid of
 * every book, a member's card naming its series and number. The Series view (#207, variant D)
 * puts series cards among the books in no series, sorted by name; clicking one opens its panel
 * full width after the last card in its row, where the book numbers are edited as one set. The
 * New project dialog, delete with a destructive confirm, and the series flows in
 * {@link SeriesActions}. Both views read again on the hub's `series` signal.
 */
export class ProjectsPage extends R2mElement {
  private readonly store = use(ProjectsStore);
  private readonly actions = use(SeriesActions);
  private readonly router = use(Router);
  private readonly confirm = use(ConfirmService);
  private readonly toast = use(ToastService);

  /** Folder of the project whose delete is in flight; its card is disabled meanwhile. */
  private readonly deleting = signal<string | null>(null);
  private readonly view = signal<ProjectsView>(readView());
  /** The series whose panel is open; one at a time. */
  private readonly openSeries = signal<string | null>(null);
  /** The open panel's tab: its books, or the series cast board. */
  private readonly panelTab = signal<SeriesPanelTab>('books');
  /** Edit book numbers: the open series' draft, while it is on. */
  private readonly draft = signal<{ seriesId: string; rows: DraftRow[] } | null>(null);
  private readonly saving = signal(false);
  /** Columns in the Series grid, measured after each render and on resize. */
  private readonly columns = signal(1);

  protected override connected(): void {
    this.classList.add('projects');
    this.onDisconnect(this.store.follow());
    // A re-read (this tab's write or the hub's series signal) rebases an open number draft, so it
    // never saves a stale set; a deleted series drops it.
    this.effect(() => {
      const series = this.store.series();
      const draft = untracked(() => this.draft());
      if (!draft) return;
      const open = series.find((s) => s.id === draft.seriesId);
      this.draft.set(open ? { seriesId: open.id, rows: rebaseDraft(draft.rows, open) } : null);
    });
    if (typeof ResizeObserver !== 'undefined') {
      const observer = new ResizeObserver(() => this.measure());
      observer.observe(this);
      this.onDisconnect(() => observer.disconnect());
    }
    // Refresh on every visit; a failure surfaces through the store's error signal, not a toast.
    void this.store.load().catch(() => undefined);
    this.arrive();
  }

  /** Series characters whose Voice editor opens on arrival (`?character=`). */
  private arrivalRows: string[] = [];

  /**
   * `?series={id}` (with `&character={scId}`): arriving from the Cast page's "Edit on the series
   * cast board" (#221), the Series view opens that series' Characters tab, with that row's Voice
   * editor open.
   */
  private arrive(): void {
    const query = this.router.query();
    const series = query.get('series');
    if (!series) return;
    this.view.set('series');
    this.openSeries.set(series);
    this.panelTab.set('characters');
    const character = query.get('character');
    this.arrivalRows = character ? [character] : [];
  }

  protected override updated(): void {
    this.measure();
  }

  private measure(): void {
    const grid = this.querySelector('.projects__grid--series');
    if (!grid) return;
    const tracks = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length;
    const columns = Math.max(1, tracks);
    if (columns !== this.columns()) this.columns.set(columns);
  }

  setView(view: ProjectsView): void {
    this.view.set(view);
    try {
      localStorage.setItem(VIEW_KEY, view);
    } catch {
      // storage unavailable — keep the in-memory value
    }
  }

  protected template() {
    const error = this.store.error();
    const view = this.view();
    // Read here, not inside `repeat`'s item templates, so the cards stay tracked (web.md, Gotchas).
    const cards: CardState = { deleting: this.deleting(), membership: this.store.membership() };
    return html`
      <header class="r2m-page-header projects__header">
        <h1 class="r2m-page-header__title">Projects</h1>
        <p class="r2m-page-header__subtitle">
          ${view === 'books' ? 'Every audiobook in the workspace.' : 'Books grouped into series.'}
        </p>
        <span class="projects__actions">
          <fieldset class="r2m-segmented projects__view" role="radiogroup" aria-label="Show">
            ${(['books', 'series'] as const).map(
              (v) =>
                html`<label>
                  <input
                    type="radio"
                    name="projects-view"
                    .value=${v}
                    .checked=${view === v}
                    @change=${() => this.setView(v)}
                  />
                  ${v === 'books' ? 'Books' : 'Series'}
                </label>`,
            )}
          </fieldset>
          ${
            view === 'series'
              ? html`<button
                  type="button"
                  class="r2m-button r2m-button--stroked projects__new-series"
                  @click=${() => void this.actions.newSeries()}
                >
                  ${icon('add')} New series
                </button>`
              : nothing
          }
          <button
            type="button"
            class="r2m-button r2m-button--filled projects__new"
            @click=${() => void this.create()}
          >
            ${icon('add')} New project
          </button>
        </span>
      </header>

      ${
        error
          ? statusChip({ status: 'error', label: `Projects could not be loaded: ${error}` })
          : nothing
      }
      ${
        this.store.loading() && !this.store.loaded()
          ? this.skeletons()
          : view === 'books'
            ? this.booksView(error, cards)
            : this.seriesView(cards)
      }
    `;
  }

  private skeletons() {
    return html`<div class="projects__grid" aria-busy="true" aria-label="Loading projects">
      ${SKELETONS.map(
        () =>
          html`<div class="projects__skeleton">
            <div class="projects__skeleton-cover"></div>
            <div class="projects__skeleton-line projects__skeleton-line--title"></div>
            <div class="projects__skeleton-line"></div>
          </div>`,
      )}
    </div>`;
  }

  private booksView(error: string | null, cards: CardState) {
    const projects = this.store.projects();
    if (projects.length === 0 && !error)
      return emptyState(
        {
          icon: 'library_books',
          headline: 'No projects yet',
          hint: 'Import an epub or text file to create your first audiobook project.',
        },
        [{ label: 'New project', icon: 'add', run: () => void this.create() }],
      );
    return html`<div class="projects__grid">
      ${repeat(
        projects,
        (p) => p.folderName,
        (p) => {
          const m = cards.membership.get(p.folderName);
          return this.bookCard(p, cards, {
            seriesLine: m ? `${m.name} · ${bookLabel(m.position)}` : undefined,
          });
        },
      )}
    </div>`;
  }

  private seriesView(cards: CardState) {
    const items = this.store.shelf();
    const series = this.store.series();
    const openId = this.openSeries();
    const openIndex = items.findIndex((i) => i.kind === 'series' && i.series.id === openId);
    const after = panelAfter(items.length, openIndex, this.columns());
    const open = series.find((s) => s.id === openId);
    const draft = this.draft();
    const saving = this.saving();
    const tab = this.panelTab();
    const empty =
      series.length === 0
        ? emptyState(
            {
              icon: 'library_books',
              headline: 'No series yet',
              hint:
                'A series groups books that share characters, so their Voices can be shared ' +
                'and the books read in order. Make one here, or use "Add to series…" on a book.',
            },
            [{ label: 'New series', icon: 'add', run: () => void this.actions.newSeries() }],
          )
        : nothing;
    return html`${empty}
    ${
      items.length > 0
        ? html`<div class="projects__grid projects__grid--series">
            ${repeat(
              items,
              (item) => item.key,
              (item, i) =>
                html`${
                  item.kind === 'series'
                    ? seriesCard(item.series, {
                        open: item.series.id === openId,
                        onToggle: () => this.toggle(item.series.id),
                        onAddBook: () => void this.actions.addBook(item.series),
                        onRename: () => void this.actions.rename(item.series),
                        onPoster: (file) => void this.actions.uploadPoster(item.series, file),
                        onRemovePoster: () => void this.actions.removePoster(item.series),
                        onDelete: () => void this.deleteSeries(item.series),
                      })
                    : this.bookCard(item.project, cards)
                }${
                  i === after && open
                    ? this.panel(
                        open,
                        draft?.seriesId === open.id ? draft.rows : null,
                        saving,
                        cards,
                        tab,
                      )
                    : nothing
                }`,
            )}
          </div>`
        : nothing
    }`;
  }

  private panel(
    series: SeriesView,
    rows: DraftRow[] | null,
    saving: boolean,
    cards: CardState,
    tab: SeriesPanelTab,
  ) {
    return seriesPanel(series, {
      tab,
      onTab: (tab) => this.panelTab.set(tab),
      characters: () =>
        html`<r2m-series-cast-board
          .series=${{ id: series.id, name: series.name }}
          .openRows=${this.arrivalRows}
        ></r2m-series-cast-board>`,
      editing: rows
        ? { blocked: saveBlockedReason(rows), problem: positionsProblem(rows), saving }
        : null,
      onEdit: () => this.draft.set({ seriesId: series.id, rows: draftRows(series) }),
      onCancel: () => this.draft.set(null),
      onSave: () => void this.saveNumbers(series),
      onAddBook: () => void this.actions.addBook(series),
      book: (member: SeriesMember) =>
        this.bookCard(member.project, cards, {
          badge: rows ? undefined : bookLabel(member.position),
          extra: rows ? this.numberBox(member, rows) : undefined,
        }),
    });
  }

  private numberBox(member: SeriesMember, rows: DraftRow[]) {
    const folder = member.project.folderName;
    const row = rows.find((r) => r.folder === folder);
    return html`<label class="projects__number">
      Book
      <input
        class="projects__number-input"
        inputmode="decimal"
        data-folder=${folder}
        aria-label=${`Book number of ${member.project.title}`}
        .value=${row?.text ?? ''}
        @input=${(e: Event) => this.type(folder, (e.target as HTMLInputElement).value)}
      />
    </label>`;
  }

  private bookCard(
    project: ProjectSummary,
    cards: CardState,
    options: { seriesLine?: string | undefined; badge?: string | undefined; extra?: unknown } = {},
  ) {
    const m = cards.membership.get(project.folderName);
    const entries = m
      ? html`${menuItem({
          label: 'Move to another series…',
          icon: 'compare_arrows',
          action: 'move-series',
          run: () => void this.actions.addToSeries(project),
        })}${menuItem({
          label: `Remove from ${m.name}`,
          icon: 'close',
          action: 'leave-series',
          run: () => void this.actions.leave(project),
        })}`
      : menuItem({
          label: 'Add to series…',
          icon: 'library_books',
          action: 'add-to-series',
          run: () => void this.actions.addToSeries(project),
        });
    return projectCard(project, {
      onOpen: () => this.open(project),
      onDelete: () => void this.delete(project),
      deleting: cards.deleting === project.folderName,
      menuEntries: entries,
      ...options,
    });
  }

  private toggle(id: string): void {
    this.draft.set(null);
    this.panelTab.set('books');
    this.openSeries.set(this.openSeries() === id ? null : id);
  }

  private type(folder: string, text: string): void {
    const draft = this.draft();
    if (!draft) return;
    this.draft.set({
      ...draft,
      rows: draft.rows.map((r) => (r.folder === folder ? { ...r, text } : r)),
    });
  }

  async saveNumbers(series: SeriesView): Promise<void> {
    const draft = this.draft();
    if (draft?.seriesId !== series.id || saveBlockedReason(draft.rows) !== null) return;
    this.saving.set(true);
    try {
      await this.store.savePositions(series.id, draftPositions(draft.rows));
      this.draft.set(null);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.saving.set(false);
    }
  }

  private async deleteSeries(series: SeriesView): Promise<void> {
    if ((await this.actions.deleteSeries(series)) && this.openSeries() === series.id)
      this.openSeries.set(null);
  }

  open(project: ProjectSummary): void {
    this.router.navigate(`projects/${encodeURIComponent(project.folderName)}`);
  }

  async create(): Promise<void> {
    const folder = await openNewProjectDialog();
    if (!folder) return;
    this.toast.success('Project created');
    this.router.navigate(`projects/${encodeURIComponent(folder)}`);
  }

  async delete(project: ProjectSummary): Promise<void> {
    const membership = this.store.membership().get(project.folderName);
    const ok = await this.confirm.confirm(deleteProjectConfirm(project.title, membership));
    if (!ok) return;
    this.deleting.set(project.folderName);
    try {
      await this.store.delete(project.folderName);
      this.toast.success(`Deleted ${project.title}`);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
    } finally {
      this.deleting.set(null);
    }
  }
}
define('r2m-projects-page', ProjectsPage);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-projects-page': ProjectsPage;
  }
}
