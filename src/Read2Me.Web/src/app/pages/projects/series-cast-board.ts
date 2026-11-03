import { html, nothing } from 'lit-html';
import { type SeriesCastDto, type SeriesCastRowDto, SeriesApi, toApiError } from '@app/api';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { signal } from '@app/core/signals';
import { LiveService } from '@app/live/live.service';
import { icon, statusChip } from '@app/ui/partials';
import { SeriesActions } from './series-actions';
import { type BoardCell, boardCells, pickerOptions } from './series-cast';
import { bookLabel } from './series-shelf';
import { adoptStyles } from '@app/core/styles';
import boardCss from './series-cast-board.css' with { type: 'text' };

adoptStyles(boardCss);

/** The series the board is for: its id and name. */
export interface BoardSeries {
  id: string;
  name: string;
}

/**
 * The series cast board (Character linking #208, variant B): the Characters tab of a series panel.
 * One row per Series character (the Narrator a row like any other), one column per book in Series
 * position order. A cell is linked (the name, and ✕ to unlink) or empty (a picker of that book's
 * unlinked Characters); the last row shares a Character as new. Read when shown, and again on the
 * hub's `series` signal for this series and after each of its own writes.
 */
export class SeriesCastBoard extends R2mElement {
  private readonly api = use(SeriesApi);
  private readonly live = use(LiveService);
  private readonly actions = use(SeriesActions);

  private readonly _series = signal<BoardSeries | null>(null);
  private readonly cast = signal<SeriesCastDto | null>(null);
  private readonly error = signal<string | null>(null);
  private readonly busy = signal(false);
  private seq = 0;

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
      this.live.on('series', (m) => {
        if (m.seriesId === this._series()?.id && m.kind === 'Changed') void this.load();
      }),
    );
    void this.load();
  }

  /** Reads the board; a slower, older read never overwrites a newer one. */
  async load(): Promise<void> {
    const series = this._series();
    if (!series) return;
    const seq = ++this.seq;
    try {
      const cast = await this.api.cast(series.id);
      if (seq !== this.seq) return;
      this.cast.set(cast);
      this.error.set(null);
    } catch (error) {
      if (seq !== this.seq) return;
      this.error.set(toApiError(error).message);
    }
  }

  /** Runs one write, then reads the board again whatever it answered. */
  private async write(run: (series: BoardSeries, cast: SeriesCastDto) => Promise<boolean>) {
    const series = this._series();
    const cast = this.cast();
    if (!series || !cast || this.busy()) return;
    this.busy.set(true);
    try {
      if (await run(series, cast)) await this.load();
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
            ${cast.rows.map((row) => this.row(cast, row, busy))} ${this.shareRow(cast, busy)}
          </tbody>
        </table>
      </div>`;
  }

  private row(cast: SeriesCastDto, row: SeriesCastRowDto, busy: boolean) {
    return html`<tr data-series-character=${row.seriesCharacterId}>
      <th scope="row" class="series-cast__label">
        ${row.isNarrator ? icon('record_voice_over') : icon('person')} ${row.label}
      </th>
      ${boardCells(cast, row).map((cell) => this.cell(cast, row, cell, busy))}
    </tr>`;
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

  /** A book's picker; a pick runs `onPick` and the select goes back to its prompt. */
  private picker(
    options: { characterId: string; name: string; isNarrator: boolean }[],
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
      ${options.map((o) => html`<option value=${o.characterId}>${o.name}</option>`)}
    </select>`;
  }
}
define('r2m-series-cast-board', SeriesCastBoard);

declare global {
  interface HTMLElementTagNameMap {
    'r2m-series-cast-board': SeriesCastBoard;
  }
}
