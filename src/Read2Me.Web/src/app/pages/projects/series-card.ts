import { html, nothing } from 'lit-html';
import { COVER_ACCEPT } from '@app/api';
import { menuItem } from '@app/ui/menu';
import { blockable, icon } from '@app/ui/partials';
import { bookLabel, type SeriesMember, type SeriesView, seriesSummary } from './series-shelf';

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
  /** A file picked through Upload/Replace poster…, not yet checked against the cover's rules. */
  onPoster: (file: File) => void;
  onRemovePoster: () => void;
  onDelete: () => void;
}

/**
 * The poster in its three states (Series lifecycle 11): the uploaded image; else the lowest-
 * positioned book's cover, marked "cover of Book N"; else stripes with the series name.
 */
function poster(series: SeriesView) {
  const { url, source, coverPosition } = series.poster;
  if (source === 'None' || !url)
    return html`<span class="projects__poster projects__poster--empty" data-poster="None">
      <span class="projects__poster-name">${series.name}</span>
    </span>`;
  const note =
    source === 'Cover' && coverPosition !== null ? `cover of ${bookLabel(coverPosition)}` : null;
  return html`<span class="projects__poster" data-poster=${source}>
    <img
      class="projects__poster-image"
      src=${url}
      alt=${note ? `${series.name}: ${note}` : `${series.name} poster`}
    />
    ${note ? html`<span class="projects__poster-note">${note}</span>` : nothing}
  </span>`;
}

/** The hidden picker the poster menu entries open; cleared after each pick so the same file can go again. */
function posterPicker(onPoster: (file: File) => void) {
  return html`<input
    class="projects__poster-input"
    type="file"
    accept=${COVER_ACCEPT}
    hidden
    tabindex="-1"
    aria-hidden="true"
    @change=${(e: Event) => {
      const input = e.currentTarget as HTMLInputElement;
      const file = input.files?.[0];
      input.value = '';
      if (file) onPoster(file);
    }}
  />`;
}

/**
 * A series on the Projects page's Series view (#207, variant D): a stacked poster with the
 * "Series" chip, the name, "4 books · Books 1–5", and the ⋮ menu. Clicking the poster or name
 * toggles its panel.
 */
export function seriesCard(series: SeriesView, options: SeriesCardOptions) {
  const menuId = menuIdFor(series.id);
  const uploaded = series.poster.source === 'Uploaded';
  // A menu entry's run gets no event, so the picker is found by the card's series id.
  const pickPoster = () =>
    document
      .querySelector<HTMLInputElement>(
        `.projects__series-card[data-series-id="${CSS.escape(series.id)}"] .projects__poster-input`,
      )
      ?.click();
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
      ${poster(series)}
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
            label: uploaded ? 'Replace poster…' : 'Upload poster…',
            icon: 'upload_file',
            action: 'upload-poster',
            run: pickPoster,
          })}
          ${
            uploaded
              ? menuItem({
                  label: 'Remove poster',
                  icon: 'hide_image',
                  action: 'remove-poster',
                  destructive: true,
                  run: options.onRemovePoster,
                })
              : nothing
          }
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
    ${posterPicker(options.onPoster)}
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
