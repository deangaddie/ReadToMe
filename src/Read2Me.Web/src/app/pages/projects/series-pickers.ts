import { html, nothing } from 'lit-html';
import type { ProjectSummary } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { adoptStyles } from '@app/core/styles';
import { icon } from '@app/ui/partials';
import { bookLabel, type Membership, nextPosition, type SeriesView } from './series-shelf';
import pickersCss from './series-pickers.css' with { type: 'text' };

adoptStyles(pickersCss);

export interface SeriesPickerData {
  book: { folder: string; title: string };
  /** Every series, sorted by name. */
  series: SeriesView[];
  membership: ReadonlyMap<string, Membership>;
}

export interface BookPickerData {
  /** The series the book joins. */
  target: SeriesView;
  projects: ProjectSummary[];
  membership: ReadonlyMap<string, Membership>;
}

/** What Add to series… picked: a series, or New series. */
export type SeriesPick = { seriesId: string } | { newSeries: true };

const bookCount = (s: SeriesView) =>
  s.members.length === 0
    ? 'No books yet'
    : `${s.members.length} ${s.members.length === 1 ? 'book' : 'books'}`;

const cancel = (close: () => void) =>
  html`<div class="r2m-dialog__actions">
    <button type="button" class="r2m-button" data-action="cancel" @click=${close}>Cancel</button>
  </div>`;

/**
 * Add to series… (#207, 7): each series but the book's own, with the number the book would join
 * as, then New series…, where it becomes Book 1. A book already in a series is told that moving
 * asks first. Closes with a {@link SeriesPick}, or undefined on Cancel.
 */
export class SeriesPicker extends R2mElement {
  data!: SeriesPickerData;

  protected template() {
    const { book, series, membership } = this.data;
    const current = membership.get(book.folder);
    const options = series.filter((s) => s.id !== current?.seriesId);
    return html`
      <h2 class="r2m-dialog__title">Add ${book.title} to a series</h2>
      <div class="r2m-dialog__content series-picker">
        ${
          current
            ? html`<p class="series-picker__current">
                Now ${bookLabel(current.position)} of ${current.name}. Moving it asks first.
              </p>`
            : nothing
        }
        ${options.map(
          (s) =>
            html`<button
              type="button"
              class="series-picker__row"
              data-series-id=${s.id}
              @click=${() => this.emit('r2m-close', { seriesId: s.id } satisfies SeriesPick)}
            >
              ${icon('library_books')}<span class="series-picker__name">${s.name}</span>
              <span class="series-picker__meta"
                >${bookCount(s)} · joins as ${bookLabel(nextPosition(s))}</span
              >
            </button>`,
        )}
        <button
          type="button"
          class="series-picker__row series-picker__row--new"
          data-action="new-series"
          @click=${() => this.emit('r2m-close', { newSeries: true } satisfies SeriesPick)}
        >
          ${icon('add')}<span class="series-picker__name">New series…</span>
          <span class="series-picker__meta">this book becomes Book 1</span>
        </button>
      </div>
      ${cancel(() => this.emit('r2m-close', undefined))}
    `;
  }
}
define('r2m-series-picker', SeriesPicker);

/**
 * Add book… from a series (#207, 7): every other book, grouped into "Not in a series" and "In
 * another series" (moving asks first). Closes with the picked folder, or undefined on Cancel.
 */
export class BookPicker extends R2mElement {
  data!: BookPickerData;

  protected template() {
    const { target, projects, membership } = this.data;
    const candidates = projects
      .filter((p) => membership.get(p.folderName)?.seriesId !== target.id)
      .sort((a, b) => a.title.localeCompare(b.title, undefined, { sensitivity: 'base' }));
    const loose = candidates.filter((p) => !membership.has(p.folderName));
    const elsewhere = candidates.filter((p) => membership.has(p.folderName));
    const row = (p: ProjectSummary) => {
      const where = membership.get(p.folderName);
      return html`<button
        type="button"
        class="series-picker__row"
        data-folder=${p.folderName}
        @click=${() => this.emit('r2m-close', p.folderName)}
      >
        ${icon('menu_book')}<span class="series-picker__name">${p.title}</span>
        ${
          where
            ? html`<span class="series-picker__meta"
                >${bookLabel(where.position)} of ${where.name} · moving asks first</span
              >`
            : p.author
              ? html`<span class="series-picker__meta">${p.author}</span>`
              : nothing
        }
      </button>`;
    };
    const group = (title: string, books: ProjectSummary[]) =>
      books.length > 0
        ? html`<h3 class="book-picker__group">${title}</h3>
            ${books.map(row)}`
        : nothing;
    return html`
      <h2 class="r2m-dialog__title">Add a book to ${target.name}</h2>
      <div class="r2m-dialog__content series-picker">
        ${
          candidates.length === 0
            ? html`<p class="book-picker__empty">
                Every book in the workspace is already in ${target.name}.
              </p>`
            : html`<p class="book-picker__note">
                  It joins as ${bookLabel(nextPosition(target))}; edit the number afterwards if it
                  belongs elsewhere.
                </p>
                ${group('Not in a series', loose)} ${group('In another series', elsewhere)}`
        }
      </div>
      ${cancel(() => this.emit('r2m-close', undefined))}
    `;
  }
}
define('r2m-book-picker', BookPicker);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-series-picker': SeriesPicker;
    'r2m-book-picker': BookPicker;
  }
}
