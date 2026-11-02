/**
 * PROTOTYPE — throwaway (#207). The Projects page with a Books / Series toggle, three Series-view
 * variants (?variant=A|B|C) over the fake store. The Books view is the same in every variant:
 * today's grid, each card naming its series and number, with series entries in its menu.
 */
import { html, nothing } from 'lit-html';
import { repeat } from 'lit-html/directives/repeat.js';
import { styleMap } from 'lit-html/directives/style-map.js';
import type { ProjectSummary } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { signal } from '@app/core/signals';
import { adoptStyles } from '@app/core/styles';
import { closeMenuOf } from '@app/ui/menu';
import { emptyState, icon } from '@app/ui/partials';
import { placeholderGradient, projectInitials } from '@app/ui/project-card';
import * as F from './proto-flows';
import * as S from './proto-store';
import protoCss from './proto.css' with { type: 'text' };
import './proto-switcher';

adoptStyles(protoCss);

let menuSeq = 0;
const menuIds = new Map<string, string>();
const menuId = (key: string) => {
  let id = menuIds.get(key);
  if (!id) menuIds.set(key, (id = `ps-menu-${++menuSeq}`));
  return id;
};

function menu(key: string, label: string, items: (close: (e: Event) => void) => unknown) {
  const id = menuId(key);
  return html`<button
      type="button"
      class="r2m-icon-button ps-more"
      popovertarget=${id}
      aria-haspopup="menu"
      aria-label=${label}
      @click=${(e: Event) => e.stopPropagation()}
    >
      ${icon('more_vert')}
    </button>
    <div id=${id} popover class="r2m-menu" role="menu" aria-label=${label}>
      ${items(closeMenuOf)}
    </div>`;
}

const menuItem = (
  close: (e: Event) => void,
  label: string,
  iconName: Parameters<typeof icon>[0],
  run: () => void,
  destructive = false,
) =>
  html`<button
    type="button"
    class="r2m-menu__item ${destructive ? 'r2m-menu__item--destructive' : ''}"
    role="menuitem"
    @click=${(e: Event) => {
      close(e);
      run();
    }}
  >
    ${icon(iconName)}<span>${label}</span>
  </button>`;

// --- Book card ---------------------------------------------------------------------------------

function cover(b: ProjectSummary) {
  return html`<button
    type="button"
    class="r2m-project-card__cover"
    style=${styleMap({ background: placeholderGradient(b.folderName) })}
    aria-label=${`Open ${b.title}`}
    @click=${() => F.openBook(b.folderName)}
  >
    <span class="r2m-project-card__initials" aria-hidden="true">${projectInitials(b.title)}</span>
  </button>`;
}

/** Today's shelf card plus a series line and series menu entries. `badge` puts "Book N" on the cover. */
function bookCard(
  b: ProjectSummary,
  opts: { seriesLine?: boolean; badge?: number | undefined; posInput?: unknown } = {},
) {
  const inSeries = S.seriesOf(b.folderName);
  const percent = b.audioItemTotal ? Math.floor((100 * b.audioItemDone) / b.audioItemTotal) : 0;
  return html`<article class="r2m-project-card ps-card" data-folder=${b.folderName}>
    <div class="ps-card__cover">
      ${cover(b)}
      ${opts.badge !== undefined ? html`<span class="ps-badge">${S.bookLabel(opts.badge)}</span>` : nothing}
    </div>
    <div class="r2m-project-card__body">
      <div class="r2m-project-card__heading">
        <button
          type="button"
          class="r2m-project-card__title"
          @click=${() => F.openBook(b.folderName)}
        >
          ${b.title}
        </button>
        ${menu(
          `book-${b.folderName}`,
          `Actions for ${b.title}`,
          (close) => html`
            ${menuItem(close, 'Open', 'open_in_new', () => F.openBook(b.folderName))}
            ${F.seriesMenuItems(b.folderName, close)}
            ${menuItem(close, 'Delete', 'delete', () => void F.deleteBook(b.folderName), true)}
          `,
        )}
      </div>
      <p class="r2m-project-card__author">${b.author}</p>
      ${
        opts.seriesLine && inSeries
          ? html`<p class="ps-series-line">
              ${icon('library_books')} ${inSeries.series.name} · ${S.bookLabel(inSeries.position)}
            </p>`
          : nothing
      }
      ${opts.posInput ?? nothing}
      ${
        b.audioItemTotal > 0
          ? html`<div class="r2m-project-card__progress">
              <div class="r2m-project-card__bar">
                <div
                  class="r2m-project-card__fill"
                  style=${styleMap({ width: `${percent}%` })}
                ></div>
              </div>
              <span class="r2m-project-card__percent">${percent}%</span>
            </div>`
          : html`<p class="r2m-project-card__unread">Not read in yet</p>`
      }
    </div>
  </article>`;
}

// --- Series poster -----------------------------------------------------------------------------

function poster(s: S.Series, size: 'card' | 'hero' | 'thumb') {
  const p = S.posterOf(s);
  const style =
    p?.kind === 'poster'
      ? { background: p.css }
      : p?.kind === 'cover'
        ? { background: placeholderGradient(p.book.folderName) }
        : {};
  return html`<div
    class="ps-poster ps-poster--${size} ${p ? '' : 'ps-poster--empty'}"
    style=${styleMap(style)}
  >
    ${
      p?.kind === 'poster'
        ? html`<span class="ps-poster__name">${s.name}</span>`
        : p?.kind === 'cover'
          ? html`<span class="r2m-project-card__initials">${projectInitials(p.book.title)}</span>
              ${size !== 'thumb' ? html`<span class="ps-poster__note">cover of ${S.bookLabel(S.membersInOrder(s)[0]!.position)}</span>` : nothing}`
          : html`<span class="ps-poster__name">${s.name}</span>`
    }
  </div>`;
}

function seriesSummary(s: S.Series): string {
  const m = S.membersInOrder(s);
  if (!m.length) return 'No books yet';
  const first = S.formatPosition(m[0]!.position);
  const last = S.formatPosition(m.at(-1)!.position);
  return `${m.length} ${m.length === 1 ? 'book' : 'books'} · ${first === last ? `Book ${first}` : `Books ${first}–${last}`}`;
}

function seriesMenu(s: S.Series, extra?: (close: (e: Event) => void) => unknown) {
  return menu(
    `series-${s.id}`,
    `Actions for ${s.name}`,
    (close) => html`
      ${menuItem(close, 'Add book…', 'add', () => void F.addBook(s.id))}
      ${extra?.(close) ?? nothing} ${menuItem(close, 'Rename…', 'edit', () => void F.rename(s.id))}
      ${menuItem(close, s.poster ? 'Replace poster…' : 'Upload poster…', 'upload_file', () => S.uploadPoster(s.id))}
      ${s.poster ? menuItem(close, 'Remove poster', 'hide_image', () => void F.clearPoster(s.id), true) : nothing}
      ${menuItem(close, 'Delete series', 'delete', () => void F.deleteSeries(s.id), true)}
    `,
  );
}

const looseSection = (title: string) => {
  const loose = S.looseBooks();
  return loose.length
    ? html`<section class="ps-section">
        <h2 class="ps-section__title">${title} <span class="ps-muted">${loose.length}</span></h2>
        <div class="projects__grid">
          ${repeat(
            loose,
            (b) => b.folderName,
            (b) => bookCard(b),
          )}
        </div>
      </section>`
    : nothing;
};

const noSeries = () =>
  emptyState(
    {
      icon: 'library_books',
      headline: 'No series yet',
      hint: 'Group books that share characters, so their Voices can be shared. Make a series here, or use "Add to series…" on a book.',
    },
    [{ label: 'New series', icon: 'add', run: () => void F.newSeries() }],
  );

// --- Variant A: series cards that expand in place ---------------------------------------------

function variantA(page: ProtoProjects) {
  const all = S.sortedSeries();
  if (!all.length) return html`${noSeries()} ${looseSection('Books not in a series')}`;
  return html`
    <div class="projects__grid ps-grid-a">
      ${all.map((s) => {
        const open = S.openSeries() === s.id;
        return html`
          <article class="ps-series-card ${open ? 'ps-series-card--open' : ''}">
            <button
              type="button"
              class="ps-series-card__open"
              aria-expanded=${open}
              @click=${() => S.openSeries.set(open ? null : s.id)}
            >
              <div class="ps-stack">${poster(s, 'card')}</div>
            </button>
            <div class="r2m-project-card__body">
              <div class="r2m-project-card__heading">
                <button
                  type="button"
                  class="r2m-project-card__title"
                  @click=${() => S.openSeries.set(open ? null : s.id)}
                >
                  ${s.name}
                </button>
                ${seriesMenu(s)}
              </div>
              <p class="r2m-project-card__author">${seriesSummary(s)}</p>
              <p class="ps-expand-hint">
                ${icon(open ? 'expand_less' : 'expand_more')} ${open ? 'Hide books' : 'Show books'}
              </p>
            </div>
          </article>
          ${open ? expandedA(page, s) : nothing}
        `;
      })}
    </div>
    ${looseSection('Books not in a series')}
  `;
}

function expandedA(page: ProtoProjects, s: S.Series) {
  const editing = page.editingA() === s.id;
  const ed = page.editorFor(s.id);
  const members = S.membersInOrder(s);
  return html`<section class="ps-expanded" aria-label=${`${s.name} books`}>
    <header class="ps-expanded__bar">
      <strong>${s.name}</strong><span class="ps-muted">${seriesSummary(s)}</span>
      <span class="ps-spacer"></span>
      ${
        editing
          ? html`${ed.problem() ? html`<span class="ps-error">${icon('error')} ${ed.problem()}</span>` : nothing}
              <button type="button" class="r2m-button" @click=${() => page.setEditingA(null)}>
                Cancel
              </button>
              <button
                type="button"
                class="r2m-button r2m-button--filled"
                ?disabled=${!!ed.problem() || !ed.dirty()}
                @click=${() => {
                  ed.save();
                  page.setEditingA(null);
                }}
              >
                ${icon('save')} Save numbers
              </button>`
          : html`<button
                type="button"
                class="r2m-button"
                ?disabled=${members.length < 1}
                @click=${() => page.setEditingA(s.id)}
              >
                ${icon('edit')} Edit book numbers
              </button>
              <button type="button" class="r2m-button" @click=${() => void F.addBook(s.id)}>
                ${icon('add')} Add book
              </button>`
      }
    </header>
    ${
      members.length
        ? html`<div class="projects__grid">
            ${members.map((m) =>
              bookCard(m.book, {
                badge: editing ? undefined : m.position,
                posInput: editing
                  ? html`<label class="ps-pos-inline">Book ${ed.input(m.book.folderName)}</label>`
                  : undefined,
              }),
            )}
          </div>`
        : html`<p class="ps-muted">
            No books yet.
            <button type="button" class="r2m-button" @click=${() => void F.addBook(s.id)}>
              Add a book
            </button>
          </p>`
    }
  </section>`;
}

// --- Variant B: series cards that open a series page ------------------------------------------

function variantB(page: ProtoProjects) {
  const openId = S.openSeries();
  const s = openId ? S.seriesById(openId) : undefined;
  if (s) return seriesPageB(page, s);
  const all = S.sortedSeries();
  return html`
    ${
      all.length
        ? html`<div class="ps-grid-b">
            ${all.map(
              (x) =>
                html`<button type="button" class="ps-tile" @click=${() => S.openSeries.set(x.id)}>
                  ${poster(x, 'card')}
                  <span class="ps-tile__name">${x.name}</span>
                  <span class="ps-muted">${seriesSummary(x)}</span>
                  <span class="ps-tile__strip">
                    ${S.membersInOrder(x).map(
                      (m) =>
                        html`<span
                          class="ps-mini"
                          style=${styleMap({ background: placeholderGradient(m.book.folderName) })}
                          data-tooltip=${`${S.bookLabel(m.position)}: ${m.book.title}`}
                          >${S.formatPosition(m.position)}</span
                        >`,
                    )}
                  </span>
                </button>`,
            )}
          </div>`
        : noSeries()
    }
    ${looseSection('Books not in a series')}
  `;
}

function seriesPageB(page: ProtoProjects, s: S.Series) {
  const ed = page.editorFor(s.id);
  const members = S.membersInOrder(s);
  return html`<section class="ps-page-b">
    <button type="button" class="r2m-button ps-back" @click=${() => S.openSeries.set(null)}>
      ${icon('arrow_back')} All series
    </button>
    <div class="ps-hero">
      ${poster(s, 'hero')}
      <div class="ps-hero__info">
        <h2 class="ps-hero__name">${s.name}</h2>
        <p class="ps-muted">${seriesSummary(s)}</p>
        <div class="ps-hero__actions">
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            @click=${() => void F.addBook(s.id)}
          >
            ${icon('add')} Add book
          </button>
          <button type="button" class="r2m-button" @click=${() => void F.rename(s.id)}>
            ${icon('edit')} Rename
          </button>
          <button type="button" class="r2m-button" @click=${() => S.uploadPoster(s.id)}>
            ${icon('upload_file')} ${s.poster ? 'Replace poster' : 'Upload poster'}
          </button>
          ${s.poster ? html`<button type="button" class="r2m-button" @click=${() => void F.clearPoster(s.id)}>${icon('hide_image')} Remove poster</button>` : nothing}
          <button
            type="button"
            class="r2m-button ps-danger"
            @click=${() => void F.deleteSeries(s.id)}
          >
            ${icon('delete')} Delete series
          </button>
        </div>
        <p class="ps-muted ps-small">
          Characters linked across these books are managed on each book's Cast page (ticket
          "Character linking").
        </p>
      </div>
    </div>
    ${
      members.length
        ? html`<table class="ps-table">
              <thead>
                <tr>
                  <th>Book</th>
                  <th>Title</th>
                  <th>Author</th>
                  <th>Audio</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                ${members.map(
                  (m) =>
                    html`<tr>
                      <td>${ed.input(m.book.folderName)}</td>
                      <td>
                        <button
                          type="button"
                          class="ps-link"
                          @click=${() => F.openBook(m.book.folderName)}
                        >
                          ${m.book.title}
                        </button>
                      </td>
                      <td class="ps-muted">${m.book.author}</td>
                      <td class="ps-muted">
                        ${m.book.audioItemTotal ? `${Math.floor((100 * m.book.audioItemDone) / m.book.audioItemTotal)}%` : 'Not read in yet'}
                      </td>
                      <td>
                        ${menu(
                          `row-${m.book.folderName}`,
                          `Actions for ${m.book.title}`,
                          (close) => html`
                            ${menuItem(close, 'Open', 'open_in_new', () => F.openBook(m.book.folderName))}
                            ${F.seriesMenuItems(m.book.folderName, close)}
                          `,
                        )}
                      </td>
                    </tr>`,
                )}
              </tbody>
            </table>
            <div class="ps-table__foot">
              ${ed.problem() ? html`<span class="ps-error">${icon('error')} ${ed.problem()}</span>` : html`<span class="ps-muted">Type a number per book; 1.5 sits between Books 1 and 2.</span>`}
              <span class="ps-spacer"></span>
              <button
                type="button"
                class="r2m-button"
                ?disabled=${!ed.dirty()}
                @click=${() => ed.reset()}
              >
                Undo changes
              </button>
              <button
                type="button"
                class="r2m-button r2m-button--filled"
                ?disabled=${!!ed.problem() || !ed.dirty()}
                @click=${() => ed.save()}
              >
                ${icon('save')} Save numbers
              </button>
            </div>`
        : html`<p class="ps-muted">No books yet.</p>`
    }
  </section>`;
}

// --- Variant C: shelves ------------------------------------------------------------------------

function variantC() {
  const all = S.sortedSeries();
  return html`
    ${all.length ? nothing : noSeries()}
    ${all.map((s) => {
      const members = S.membersInOrder(s);
      return html`<section class="ps-shelf">
        <header class="ps-shelf__head">
          ${poster(s, 'thumb')}
          <div class="ps-shelf__title">
            <h2>${s.name}</h2>
            <span class="ps-muted">${seriesSummary(s)}</span>
          </div>
          <span class="ps-spacer"></span>
          <button
            type="button"
            class="r2m-button"
            ?disabled=${!members.length}
            @click=${() => void editPositionsC(s.id)}
          >
            ${icon('edit')} Book numbers
          </button>
          <button type="button" class="r2m-button" @click=${() => void F.addBook(s.id)}>
            ${icon('add')} Add book
          </button>
          ${seriesMenu(s)}
        </header>
        <div class="ps-shelf__row">
          ${members.map((m) => bookCard(m.book, { badge: m.position }))}
          <button type="button" class="ps-slot" @click=${() => void F.addBook(s.id)}>
            ${icon('add')}<span>Add a book</span>
          </button>
        </div>
      </section>`;
    })}
    ${
      S.looseBooks().length
        ? html`<section class="ps-shelf ps-shelf--loose">
            <header class="ps-shelf__head">
              <div class="ps-shelf__title">
                <h2>Not in a series</h2>
                <span class="ps-muted">${S.looseBooks().length} books</span>
              </div>
            </header>
            <div class="ps-shelf__row">${S.looseBooks().map((b) => bookCard(b))}</div>
          </section>`
        : nothing
    }
  `;
}

async function editPositionsC(id: string): Promise<void> {
  const d = document.createElement('r2m-proto-positions-dialog');
  d.seriesId = id;
  await openDialog(d);
}

// --- Page --------------------------------------------------------------------------------------

export class ProtoProjects extends R2mElement {
  readonly editingA = signal<string | null>(null);
  private readonly editors = new Map<string, F.PositionsEditor>();

  editorFor(id: string): F.PositionsEditor {
    let e = this.editors.get(id);
    if (!e) this.editors.set(id, (e = new F.PositionsEditor(() => S.seriesById(id)!)));
    return e;
  }

  setEditingA(id: string | null): void {
    if (id) this.editorFor(id).reset();
    this.editingA.set(id);
  }

  protected override connected(): void {
    // Drafts follow the store when it changes elsewhere (join, leave, scenario).
    this.effect(() => {
      S.series();
      for (const [id, e] of this.editors) if (S.seriesById(id) && !e.dirty()) e.reset();
    });
  }

  protected template() {
    const v = S.view();
    const toggle = html`<div class="ps-toggle" role="radiogroup" aria-label="Show">
      ${(['books', 'series'] as const).map(
        (x) =>
          html`<button
            type="button"
            role="radio"
            aria-checked=${v === x}
            class="ps-toggle__btn ${v === x ? 'ps-toggle__btn--on' : ''}"
            @click=${() => S.setView(x)}
          >
            ${icon(x === 'books' ? 'menu_book' : 'library_books')}
            ${x === 'books' ? 'Books' : 'Series'}
          </button>`,
      )}
    </div>`;
    return html`
      <header class="r2m-page-header projects__header ps-header">
        <h1 class="r2m-page-header__title">Projects</h1>
        <p class="r2m-page-header__subtitle">
          ${v === 'books' ? 'Every audiobook in the workspace.' : 'Books grouped into series.'}
        </p>
        <span class="projects__actions ps-actions">
          ${toggle}
          ${
            v === 'series'
              ? html`<button type="button" class="r2m-button" @click=${() => void F.newSeries()}>
                  ${icon('add')} New series
                </button>`
              : nothing
          }
          <button
            type="button"
            class="r2m-button r2m-button--filled"
            @click=${() => F.openBook('study-in-scarlet')}
          >
            ${icon('add')} New project
          </button>
        </span>
      </header>
      ${
        v === 'books'
          ? html`<div class="projects__grid">
              ${repeat(
                S.sortedBooks(),
                (b) => b.folderName,
                (b) => bookCard(b, { seriesLine: true }),
              )}
            </div>`
          : S.variant() === 'A'
            ? variantA(this)
            : S.variant() === 'B'
              ? variantB(this)
              : variantC()
      }
    `;
  }
}
define('r2m-proto-projects', ProtoProjects);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-projects': ProtoProjects;
  }
}
