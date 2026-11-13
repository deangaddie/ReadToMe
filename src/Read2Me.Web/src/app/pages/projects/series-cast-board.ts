import { html, nothing } from 'lit-html';
import {
  type SeriesCastDto,
  type SeriesCastRowDto,
  type SeriesSuggestedRowDto,
  toApiError,
} from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { SeriesStore, rereads } from '@app/series/series-store';
import { icon, statusChip } from '@app/ui/partials';
import { SeriesActions } from './series-actions';
import { type BoardCell, boardCells, pickerOptions, suggestedRowCells } from './series-cast';
import { bookLabel } from './series-shelf';
import './series-voice-editor';
import { adoptStyles } from '@app/core/styles';
import boardCss from './series-cast-board.css' with { type: 'text' };

adoptStyles(boardCss);

/** `cast-read`: the board read its series again; the panel's Characters tab shows the count (#223). */
export interface CastRead {
  seriesId: string;
  suggestionCount: number;
}

/** The series the board is for: its id and name. */
export interface BoardSeries {
  id: string;
  name: string;
}

/**
 * The series cast board (Character linking #208, variant B): the Characters tab of a series panel.
 * One row per Series character (the Narrator a row like any other), one column per book in Series
 * position order. A cell is linked (the name, and ✕ to unlink) or empty (a picker of that book's
 * unlinked Characters); the last row shares a Character as new. A row's label opens its inline
 * series Voice editor under it (#221), several at once. Read when shown, and again on each
 * {@link SeriesStore} change naming this series (its own writes included) and on `Resynced`.
 */
export class SeriesCastBoard extends R2mElement {
  private readonly seriesStore = use(SeriesStore);
  private readonly actions = use(SeriesActions);

  private readonly _series = signal<BoardSeries | null>(null);
  private readonly cast = signal<SeriesCastDto | null>(null);
  private readonly error = signal<string | null>(null);
  private readonly busy = signal(false);
  /** The rows whose inline series Voice editor is open (#221), by Series character. */
  readonly open = signal<ReadonlySet<string>>(new Set());
  private seq = 0;

  /** Opens these rows' Voice editors on arrival (the Cast page's "Edit on the series cast board"). */
  get openRows(): readonly string[] {
    return [...this.open()];
  }
  set openRows(ids: readonly string[]) {
    if (ids.length > 0) this.open.set(new Set([...this.open(), ...ids]));
  }

  get series(): BoardSeries | null {
    return this._series();
  }
  set series(value: BoardSeries | null) {
    const changed = value?.id !== this._series()?.id;
    this._series.set(value);
    if (changed) {
      this.cast.set(null);
      if (this.isConnected) void this.load();
    }
  }

  protected override connected(): void {
    this.classList.add('series-cast');
    this.onDisconnect(
      this.seriesStore.changes((change) =>
        rereads(change, this._series()?.id) ? this.load() : undefined,
      ),
    );
    void this.load();
  }

  /** Reads the board; a slower, older read never overwrites a newer one. */
  async load(): Promise<void> {
    const series = this._series();
    if (!series) return;
    const seq = ++this.seq;
    try {
      const cast = await this.seriesStore.cast(series.id);
      if (seq !== this.seq) return;
      this.cast.set(cast);
      this.error.set(null);
      this.emit<CastRead>('cast-read', {
        seriesId: series.id,
        suggestionCount: cast.suggestionCount,
      });
    } catch (error) {
      if (seq !== this.seq) return;
      this.error.set(toApiError(error).message);
    }
  }

  /** Runs one write; `SeriesStore` reads the board again before it resolves. */
  private async write(run: (series: BoardSeries, cast: SeriesCastDto) => Promise<boolean>) {
    const series = this._series();
    const cast = this.cast();
    if (!series || !cast || this.busy()) return;
    this.busy.set(true);
    try {
      await run(series, cast);
    } finally {
      this.busy.set(false);
    }
  }

  protected template() {
    const cast = this.cast();
    const error = this.error();
    if (error && !cast)
      return statusChip({ status: 'error', label: `The series cast could not be read: ${error}` });
    if (!cast) return html`<p class="series-cast__note" aria-busy="true">Reading the cast…</p>`;
    if (cast.books.length === 0)
      return html`<p class="series-cast__note">
        No books yet. Add books to the series to link their Characters.
      </p>`;
    const busy = this.busy();
    return html`${
        cast.rows.length === 0
          ? html`<p class="series-cast__note">
              No series characters yet. Share a Character from one book to start a row; the
              Characters of the other books can then be linked to it, and share its Voices.
            </p>`
          : nothing
      }
      <div class="series-cast__scroll">
        <table class="series-cast__table" aria-busy=${busy ? 'true' : 'false'}>
          <thead>
            <tr>
              <th scope="col">Series character</th>
              ${cast.books.map(
                (b) =>
                  html`<th scope="col" data-folder=${b.folder}>
                    ${bookLabel(b.position)}<span class="series-cast__book">${b.title}</span>
                  </th>`,
              )}
            </tr>
          </thead>
          <tbody>
            ${cast.rows.map((row) => this.row(cast, row, busy))}
            ${cast.suggestedRows.map((row) => this.suggestedRow(cast, row, busy))}
            ${this.shareRow(cast, busy)}
          </tbody>
        </table>
      </div>`;
  }

  /** Opens or closes a row's inline series Voice editor; several stay open at once. */
  toggle(seriesCharacterId: string): void {
    this.open.update((open) => {
      const next = new Set(open);
      if (!next.delete(seriesCharacterId)) next.add(seriesCharacterId);
      return next;
    });
  }

  private row(cast: SeriesCastDto, row: SeriesCastRowDto, busy: boolean) {
    const series = this._series();
    const open = this.open().has(row.seriesCharacterId);
    return html`<tr data-series-character=${row.seriesCharacterId}>
        <th scope="row" class="series-cast__label">
          <button
            type="button"
            class="series-cast__toggle"
            data-action="voices"
            aria-expanded=${open ? 'true' : 'false'}
            data-tooltip=${open ? 'Close its Voices' : 'Edit its Voices'}
            @click=${() => this.toggle(row.seriesCharacterId)}
          >
            ${icon(open ? 'expand_less' : 'expand_more')}
            ${row.isNarrator ? icon('record_voice_over') : icon('person')} ${row.label}
          </button>
        </th>
        ${boardCells(cast, row).map((cell) => this.cell(cast, row, cell, busy))}
      </tr>
      ${
        open && series
          ? html`<tr class="series-cast__editor-row" data-editor-for=${row.seriesCharacterId}>
              <td colspan=${cast.books.length + 1}>
                <r2m-series-voice-editor
                  .seriesId=${series.id}
                  .seriesCharacterId=${row.seriesCharacterId}
                  .label=${row.label}
                ></r2m-series-voice-editor>
              </td>
            </tr>`
          : nothing
      }`;
  }

  private cell(cast: SeriesCastDto, row: SeriesCastRowDto, cell: BoardCell, busy: boolean) {
    const folder = cell.book.folder;
    switch (cell.kind) {
      case 'linked':
        return html`<td data-folder=${folder} data-cell="linked">
          <span class="series-cast__linked">
            ${icon('link')} <span class="series-cast__name">${cell.name}</span>
            ${
              cell.differs
                ? html`<span class="series-cast__differs" data-tooltip="Differs from series"
                    >${icon('sync_problem')}</span
                  >`
                : nothing
            }
            <button
              type="button"
              class="r2m-icon-button series-cast__unlink"
              data-action="unlink"
              aria-label=${`Unlink ${cell.name}`}
              ?disabled=${busy}
              @click=${() =>
                void this.write((series) =>
                  this.actions.unlink(series, cast, row, folder, cell.name),
                )}
            >
              ${icon('close')}
            </button>
          </span>
        </td>`;
      case 'suggested':
        return html`<td data-folder=${folder} data-cell="suggested">
          <span class="series-cast__suggested">${cell.name}?</span>
          <button
            type="button"
            class="r2m-button"
            data-action="link-suggested"
            ?disabled=${busy}
            @click=${() =>
              void this.write((series) =>
                this.actions.link(series, cast, row, folder, {
                  characterId: cell.characterId,
                  name: cell.name,
                }),
              )}
          >
            Link
          </button>
          <button
            type="button"
            class="r2m-icon-button series-cast__dismiss"
            data-action="not-the-same"
            aria-label=${`${cell.name} is not ${row.label}`}
            data-tooltip="Not the same"
            ?disabled=${busy}
            @click=${() =>
              void this.write((series) =>
                this.actions.notTheSame(series, row, folder, cell.characterId),
              )}
          >
            ${icon('close')}
          </button>
        </td>`;
      case 'empty':
        return html`<td data-folder=${folder} data-cell="empty">
          ${this.picker(
            cell.options,
            `Link a Character of ${cell.book.title} to ${row.label}`,
            'Link…',
            busy,
            (character) =>
              void this.write((series) => this.actions.link(series, cast, row, folder, character)),
          )}
        </td>`;
    }
  }

  /** The last row: pick a Character of any book to share it as a new Series character. */
  private shareRow(cast: SeriesCastDto, busy: boolean) {
    return html`<tr class="series-cast__share-row">
      <th scope="row" class="series-cast__label series-cast__label--new">
        ${icon('person_add')} Share as new
      </th>
      ${cast.books.map(
        (b) =>
          html`<td data-folder=${b.folder} data-cell="share">
            ${this.picker(
              pickerOptions(cast, b.folder),
              `Share a Character of ${b.title} as a new series character`,
              'Share…',
              busy,
              (character) =>
                void this.write((series) => this.actions.share(series, cast, b.folder, character)),
            )}
          </td>`,
      )}
    </tr>`;
  }

  /**
   * A suggested row (#223): unlinked Characters of two or more books whose names match. Share and
   * link makes them one Series character; ✕ records each pair as not the same.
   */
  private suggestedRow(cast: SeriesCastDto, row: SeriesSuggestedRowDto, busy: boolean) {
    const key = row.members.map((m) => m.characterId).join(' ');
    return html`<tr class="series-cast__suggested-row" data-suggested-row=${key}>
      <th scope="row" class="series-cast__label">
        <span class="series-cast__suggested">${row.label}?</span>
        <button
          type="button"
          class="r2m-button"
          data-action="share-and-link"
          ?disabled=${busy}
          @click=${() => void this.write((series) => this.actions.shareAndLink(series, cast, row))}
        >
          Share and link
        </button>
        <button
          type="button"
          class="r2m-icon-button series-cast__dismiss"
          data-action="not-the-same-row"
          aria-label=${`These are not the same ${row.label}`}
          data-tooltip="Not the same"
          ?disabled=${busy}
          @click=${() => void this.write((series) => this.actions.notTheSameRow(series, row))}
        >
          ${icon('close')}
        </button>
      </th>
      ${suggestedRowCells(cast, row).map(
        ({ book, member }) =>
          html`<td data-folder=${book.folder} data-cell=${member ? 'member' : 'none'}>
            ${
              member
                ? html`<span class="series-cast__suggested">${member.name}?</span>`
                : html`<span class="series-cast__none">—</span>`
            }
          </td>`,
      )}
    </tr>`;
  }

  /** A book's picker; a pick runs `onPick` and the select goes back to its prompt. Name matches come first, marked. */
  private picker(
    options: { characterId: string; name: string; isNarrator: boolean; matches?: boolean }[],
    label: string,
    prompt: string,
    busy: boolean,
    onPick: (character: { characterId: string; name: string }) => void,
  ) {
    if (options.length === 0) return html`<span class="series-cast__none">—</span>`;
    return html`<select
      class="series-cast__picker"
      aria-label=${label}
      ?disabled=${busy}
      @change=${(e: Event) => {
        const select = e.currentTarget as HTMLSelectElement;
        const picked = options.find((o) => o.characterId === select.value);
        select.value = '';
        if (picked) onPick({ characterId: picked.characterId, name: picked.name });
      }}
    >
      <option value="">${prompt}</option>
      ${options.map(
        (o) =>
          html`<option value=${o.characterId} data-matches=${o.matches ? 'true' : 'false'}>
            ${o.matches ? `${o.name} — name matches` : o.name}
          </option>`,
      )}
    </select>`;
  }
}
define('r2m-series-cast-board', SeriesCastBoard);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-series-cast-board': SeriesCastBoard;
  }
}
