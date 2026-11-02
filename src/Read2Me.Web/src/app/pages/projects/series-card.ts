import { html, nothing } from 'lit-html';
import { menuItem } from '@app/ui/menu';
import { blockable, icon } from '@app/ui/partials';
import { type SeriesMember, type SeriesView, seriesSummary } from './series-shelf';

/** One popover id per series, stable across renders so the trigger and its menu always pair up. */
const menuIds = new Map<string, string>();
function menuIdFor(id: string): string {
  let menuId = menuIds.get(id);
  if (!menuId) menuIds.set(id, (menuId = `r2m-series-card-menu-${menuIds.size + 1}`));
  return menuId;
}

export interface SeriesCardOptions {
  open: boolean;
  onToggle: () => void;
  onAddBook: () => void;
  onRename: () => void;
  onDelete: () => void;
}

/**
 * A series on the Projects page's Series view (#207, variant D): a stacked poster with the
 * "Series" chip, the name, "4 books · Books 1–5", and the ⋮ menu. Clicking the poster or name
 * toggles its panel. The poster is a placeholder with the name until the series poster slice.
 */
export function seriesCard(series: SeriesView, options: SeriesCardOptions) {
  const menuId = menuIdFor(series.id);
  return html`<article
    class="projects__series-card ${options.open ? 'projects__series-card--open' : ''}"
    data-series-id=${series.id}
  >
    <button
      type="button"
      class="projects__series-open"
      aria-expanded=${options.open}
      aria-label=${`${options.open ? 'Hide' : 'Show'} the books of ${series.name}`}
      @click=${options.onToggle}
    >
      <span class="projects__poster">
        <span class="projects__poster-name">${series.name}</span>
      </span>
      <span class="projects__series-chip">${icon('library_books')} Series</span>
    </button>
    <div class="r2m-project-card__body">
      <div class="r2m-project-card__heading">
        <button
          type="button"
          class="r2m-project-card__title"
          data-tooltip=${series.name}
          @click=${options.onToggle}
        >
          ${series.name}
        </button>
        <button
          type="button"
          class="r2m-icon-button r2m-project-card__more"
          popovertarget=${menuId}
          aria-haspopup="menu"
          aria-label=${`Actions for ${series.name}`}
        >
          ${icon('more_vert')}
        </button>
        <div
          id=${menuId}
          popover
          class="r2m-menu"
          role="menu"
          aria-label=${`Actions for ${series.name}`}
        >
          ${menuItem({ label: 'Add book…', icon: 'add', action: 'add-book', run: options.onAddBook })}
          ${menuItem({ label: 'Rename…', icon: 'edit', action: 'rename-series', run: options.onRename })}
          ${menuItem({
            label: 'Delete series',
            icon: 'delete',
            action: 'delete-series',
            destructive: true,
            run: options.onDelete,
          })}
        </div>
      </div>
      <p class="r2m-project-card__author projects__series-summary">${seriesSummary(series)}</p>
    </div>
  </article>`;
}

export interface SeriesPanelOptions {
  /** While Edit book numbers is on: Save's blocked reason (null when it can save). */
  editing: { blocked: string | null; problem: string | null; saving: boolean } | null;
  onEdit: () => void;
  onCancel: () => void;
  onSave: () => void;
  onAddBook: () => void;
  /** One member's card, with its badge or its number box. */
  book: (member: SeriesMember) => unknown;
}

/**
 * The open series' full-width panel, placed after the last card in its row (#207, 3): the member
 * books in Series position order, Edit book numbers and Add book; while editing, Cancel and
 * Save numbers, which is off with the set's problem or when nothing changed.
 */
export function seriesPanel(series: SeriesView, options: SeriesPanelOptions) {
  const editing = options.editing;
  return html`<section
    class="projects__panel"
    data-series-id=${series.id}
    aria-label=${`Books in ${series.name}`}
  >
    <header class="projects__panel-bar">
      <strong class="projects__panel-name">${series.name}</strong>
      <span class="projects__panel-summary">${seriesSummary(series)}</span>
      <span class="projects__panel-spacer"></span>
      ${
        editing
          ? html`${
                editing.problem
                  ? html`<span class="projects__panel-problem" role="alert"
                      >${icon('error')} ${editing.problem}</span
                    >`
                  : nothing
              }
              <button
                type="button"
                class="r2m-button"
                data-action="cancel-numbers"
                @click=${options.onCancel}
              >
                Cancel
              </button>
              ${blockable(
                editing.saving ? null : editing.blocked,
                html`<button
                  type="button"
                  class="r2m-button r2m-button--filled"
                  data-action="save-numbers"
                  ?disabled=${editing.blocked !== null || editing.saving}
                  @click=${options.onSave}
                >
                  ${icon('save')} Save numbers
                </button>`,
              )}`
          : html`${
                series.members.length > 0
                  ? html`<button
                      type="button"
                      class="r2m-button"
                      data-action="edit-numbers"
                      @click=${options.onEdit}
                    >
                      ${icon('edit')} Edit book numbers
                    </button>`
                  : nothing
              }
              <button
                type="button"
                class="r2m-button"
                data-action="panel-add-book"
                @click=${options.onAddBook}
              >
                ${icon('add')} Add book
              </button>`
      }
    </header>
    ${
      series.members.length > 0
        ? html`<div class="projects__panel-grid">${series.members.map(options.book)}</div>`
        : html`<p class="projects__panel-empty">
            No books yet. Add a book to start the series; it becomes Book 1.
          </p>`
    }
  </section>`;
}
