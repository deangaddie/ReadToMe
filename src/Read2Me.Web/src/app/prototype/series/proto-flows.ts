/**
 * PROTOTYPE — throwaway. The user-facing flows every variant shares: pick a series, name a
 * series, and the confirms "Series lifecycle" (#206) settled (Leave, Move to, delete series, clear
 * poster, delete a member project). Joining, renaming, replacing a poster and saving positions
 * ask nothing.
 */
import { html, nothing } from 'lit-html';
import { openDialog } from '@app/core/dialog';
import { R2mElement, define } from '@app/core/element';
import { use } from '@app/core/services';
import { computed, signal } from '@app/core/signals';
import { ConfirmService, PromptService } from '@app/ui/dialogs';
import { icon } from '@app/ui/partials';
import { ToastService } from '@app/ui/toast';
import * as S from './proto-store';

/** Fake series-linked Characters per series, so the Leave confirm has names to show. */
const LINKED: Record<string, string[]> = {
  's-holmes': ['Sherlock Holmes', 'John Watson', 'Inspector Lestrade'],
  's-oz': ['Dorothy', 'The Scarecrow', 'The Tin Woodman'],
  's-tarzan': ['Tarzan', 'Jane Porter'],
};
const linkedIn = (seriesId: string) => LINKED[seriesId] ?? [];

const confirm = () => use(ConfirmService);
const toast = () => use(ToastService);

/** Asks for a unique name, re-asking with the problem until it is fine or cancelled. */
async function askName(title: string, initial = '', exceptId?: string): Promise<string | null> {
  let value = initial;
  for (;;) {
    const name = await use(PromptService).text({
      title,
      label: 'Series name',
      initial: value,
      required: true,
      confirmLabel: title.startsWith('Rename') ? 'Rename' : 'Create series',
    });
    if (name === null) return null;
    const problem = S.nameProblem(name, exceptId);
    if (!problem) return name;
    toast().error(problem);
    value = name;
  }
}

export async function newSeries(withFolder?: string): Promise<S.Series | null> {
  const name = await askName('New series');
  return name ? S.createSeries(name, withFolder) : null;
}

export async function rename(id: string): Promise<void> {
  const s = S.seriesById(id)!;
  const name = await askName(`Rename ${s.name}`, s.name, id);
  if (name && name !== s.name) S.renameSeries(id, name);
}

async function confirmLeave(folder: string, moveTo?: S.Series): Promise<boolean> {
  const from = S.seriesOf(folder)!;
  const title = S.bookOf(folder).title;
  const linked = linkedIn(from.series.id);
  const links = linked.length
    ? `${linked.join(', ')} lose their series link. This book keeps its own copy of their Voices; linking them again later asks to merge Voices.`
    : 'No Characters in this book are linked to the series.';
  return confirm().confirm({
    title: moveTo
      ? `Move ${title} to ${moveTo.name}?`
      : `Remove ${title} from ${from.series.name}?`,
    message: `${links} The other books in ${from.series.name} keep their numbers.`,
    confirmLabel: moveTo ? `Move to ${moveTo.name}` : 'Remove from series',
  });
}

export async function leave(folder: string): Promise<void> {
  if (await confirmLeave(folder)) S.leave(folder);
}

/** Add to series… for a book: pick an existing series or make a new one. */
export async function addToSeries(folder: string): Promise<void> {
  const dialog = document.createElement('r2m-proto-pick-series');
  dialog.folder = folder;
  const choice = await openDialog<string>(dialog);
  if (!choice) return;
  if (choice === '__new') {
    if (S.seriesOf(folder) && !(await confirmLeave(folder))) return;
    if (S.seriesOf(folder)) S.leave(folder);
    const s = await newSeries(folder);
    if (s) toast().success(`${S.bookOf(folder).title} is Book 1 of ${s.name}`);
    return;
  }
  const target = S.seriesById(choice)!;
  if (S.seriesOf(folder) && !(await confirmLeave(folder, target))) return;
  const position = S.join(choice, folder);
  toast().success(
    `${S.bookOf(folder).title} is ${S.bookLabel(position)} of ${target.name}. Edit the number if it belongs elsewhere.`,
  );
}

/** Add book… from a series: pick a book. Books in another series show where they are now. */
export async function addBook(seriesId: string): Promise<void> {
  const dialog = document.createElement('r2m-proto-pick-book');
  dialog.seriesId = seriesId;
  const folder = await openDialog<string>(dialog);
  if (!folder) return;
  const target = S.seriesById(seriesId)!;
  if (S.seriesOf(folder) && !(await confirmLeave(folder, target))) return;
  const position = S.join(seriesId, folder);
  toast().success(`${S.bookOf(folder).title} is ${S.bookLabel(position)}`);
}

export async function deleteSeries(id: string): Promise<void> {
  const s = S.seriesById(id)!;
  const titles = S.membersInOrder(s).map((m) => m.book.title);
  const ok = await confirm().confirm({
    title: `Delete series ${s.name}?`,
    message:
      (titles.length
        ? `Every book is kept, with its Characters and Voices: ${titles.join(', ')}. They just stop being a series. `
        : 'The series has no books. ') +
      'Its name, numbers, links and poster are removed and cannot be restored.',
    destructive: true,
    confirmLabel: 'Delete series',
  });
  if (ok) S.deleteSeries(id);
}

export async function clearPoster(id: string): Promise<void> {
  const s = S.seriesById(id)!;
  const ok = await confirm().confirm({
    title: `Remove the poster of ${s.name}?`,
    message:
      "The uploaded image is deleted; there is no History to bring it back. The series shows the lowest-numbered book's cover instead.",
    destructive: true,
    confirmLabel: 'Remove poster',
  });
  if (ok) S.clearPoster(id);
}

export async function deleteBook(folder: string): Promise<void> {
  const b = S.bookOf(folder);
  const inSeries = S.seriesOf(folder);
  const ok = await confirm().confirm({
    title: `Delete ${b.title}?`,
    message:
      'The project folder and everything in it are removed: the book file, characters, voices and generated audio. This cannot be undone.' +
      (inSeries
        ? ` Part of ${inSeries.series.name} (${S.bookLabel(inSeries.position)}); the other books keep their Voices.`
        : ''),
    destructive: true,
    confirmLabel: 'Delete project',
  });
  if (ok) S.deleteBook(folder);
}

export function openBook(folder: string): void {
  toast().info(`Would open ${S.bookOf(folder).title} (prototype: fake project)`);
}

/** The per-book menu entries for series membership, shared by every card. */
export function seriesMenuItems(folder: string, close: (e: Event) => void) {
  const inSeries = S.seriesOf(folder);
  const item = (label: string, iconName: Parameters<typeof icon>[0], run: () => void) =>
    html`<button
      type="button"
      class="r2m-menu__item"
      role="menuitem"
      @click=${(e: Event) => {
        close(e);
        run();
      }}
    >
      ${icon(iconName)}<span>${label}</span>
    </button>`;
  return inSeries
    ? html`${item(`Move to another series…`, 'compare_arrows', () => void addToSeries(folder))}
      ${item(`Remove from ${inSeries.series.name}`, 'block', () => void leave(folder))}`
    : item('Add to series…', 'library_books', () => void addToSeries(folder));
}

// --- Pickers ----------------------------------------------------------------------------------

export class PickSeries extends R2mElement {
  folder!: string;
  protected template() {
    const current = S.seriesOf(this.folder);
    const options = S.sortedSeries().filter((s) => s.id !== current?.series.id);
    return html`
      <h2 class="r2m-dialog__title">Add ${S.bookOf(this.folder).title} to a series</h2>
      <div class="r2m-dialog__content ps-pick">
        ${
          current
            ? html`<p class="ps-muted">
                Now ${S.bookLabel(current.position)} of ${current.series.name}. Moving it asks
                first.
              </p>`
            : nothing
        }
        ${options.map(
          (s) =>
            html`<button
              type="button"
              class="ps-pick__row"
              @click=${() => this.emit('r2m-close', s.id)}
            >
              ${icon('library_books')}<span class="ps-pick__name">${s.name}</span>
              <span class="ps-muted"
                >${s.members.length} books · joins as Book
                ${
                  s.members.length
                    ? Math.floor(Math.max(...s.members.map((m) => m.position))) + 1
                    : 1
                }</span
              >
            </button>`,
        )}
        <button
          type="button"
          class="ps-pick__row ps-pick__row--new"
          @click=${() => this.emit('r2m-close', '__new')}
        >
          ${icon('add')}<span class="ps-pick__name">New series…</span>
          <span class="ps-muted">this book becomes Book 1</span>
        </button>
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', undefined)}>
          Cancel
        </button>
      </div>
    `;
  }
}
define('r2m-proto-pick-series', PickSeries);

export class PickBook extends R2mElement {
  seriesId!: string;
  protected template() {
    const target = S.seriesById(this.seriesId)!;
    const candidates = S.sortedBooks().filter(
      (b) => !target.members.some((m) => m.folder === b.folderName),
    );
    const loose = candidates.filter((b) => !S.seriesOf(b.folderName));
    const elsewhere = candidates.filter((b) => S.seriesOf(b.folderName));
    const row = (b: (typeof candidates)[number]) => {
      const where = S.seriesOf(b.folderName);
      return html`<button
        type="button"
        class="ps-pick__row"
        @click=${() => this.emit('r2m-close', b.folderName)}
      >
        ${icon('menu_book')}<span class="ps-pick__name">${b.title}</span>
        <span class="ps-muted"
          >${where ? `in ${where.series.name} · moving asks first` : (b.author ?? '')}</span
        >
      </button>`;
    };
    return html`
      <h2 class="r2m-dialog__title">Add a book to ${target.name}</h2>
      <div class="r2m-dialog__content ps-pick">
        <p class="ps-muted">
          It joins as Book
          ${
            target.members.length
              ? Math.floor(Math.max(...target.members.map((m) => m.position))) + 1
              : 1
          };
          edit the number afterwards if it belongs elsewhere.
        </p>
        ${
          loose.length
            ? html`<h3 class="ps-pick__group">Not in a series</h3>
                ${loose.map(row)}`
            : nothing
        }
        ${
          elsewhere.length
            ? html`<h3 class="ps-pick__group">In another series</h3>
                ${elsewhere.map(row)}`
            : nothing
        }
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', undefined)}>
          Cancel
        </button>
      </div>
    `;
  }
}
define('r2m-proto-pick-book', PickBook);

/** Variant C: the positions of one series in a dialog, saved as one set. */
export class PositionsDialog extends R2mElement {
  seriesId!: string;
  private readonly editor = new PositionsEditor(() => S.seriesById(this.seriesId)!);
  protected template() {
    const s = S.seriesById(this.seriesId)!;
    return html`
      <h2 class="r2m-dialog__title">Book numbers in ${s.name}</h2>
      <div class="r2m-dialog__content">
        <p class="ps-muted">
          Type each book's number: 1.5 sits between Books 1 and 2, gaps are fine, no two books can
          share one.
        </p>
        ${this.editor.rows()}
      </div>
      <div class="r2m-dialog__actions">
        <button type="button" class="r2m-button" @click=${() => this.emit('r2m-close', undefined)}>
          Cancel
        </button>
        <button
          type="button"
          class="r2m-button r2m-button--filled"
          ?disabled=${!!this.editor.problem()}
          @click=${() => {
            this.editor.save();
            this.emit('r2m-close', true);
          }}
        >
          Save numbers
        </button>
      </div>
    `;
  }
}
define('r2m-proto-positions-dialog', PositionsDialog);

/** Draft of a series' positions: typed text per book, checked as a whole set. */
export class PositionsEditor {
  readonly draft = signal<{ folder: string; text: string }[]>([]);
  readonly problem = computed(() => S.positionsProblem(this.draft()));
  readonly dirty = computed(() => {
    const s = this.series();
    return this.draft().some(
      (d) => s.members.find((m) => m.folder === d.folder)?.position !== Number(d.text.trim()),
    );
  });
  constructor(private readonly series: () => S.Series) {
    this.reset();
  }
  reset(): void {
    this.draft.set(
      S.membersInOrder(this.series()).map((m) => ({
        folder: m.book.folderName,
        text: S.formatPosition(m.position),
      })),
    );
  }
  set(folder: string, text: string): void {
    this.draft.update((d) => d.map((x) => (x.folder === folder ? { ...x, text } : x)));
  }
  textOf(folder: string): string {
    return this.draft().find((d) => d.folder === folder)?.text ?? '';
  }
  save(): void {
    S.savePositions(this.series().id, this.draft());
    this.reset();
  }
  input(folder: string) {
    return html`<input
      class="ps-pos-input"
      inputmode="decimal"
      aria-label=${`Number of ${S.bookOf(folder).title}`}
      .value=${this.textOf(folder)}
      @input=${(e: Event) => this.set(folder, (e.target as HTMLInputElement).value)}
    />`;
  }
  rows() {
    return html`<div class="ps-pos-list">
        ${this.draft().map(
          (d) =>
            html`<label class="ps-pos-row"
              ><span class="ps-pos-row__book">Book</span>${this.input(d.folder)}<span
                >${S.bookOf(d.folder).title}</span
              ></label
            >`,
        )}
      </div>
      ${this.problem() ? html`<p class="ps-error">${icon('error')} ${this.problem()}</p>` : nothing}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    'r2m-proto-pick-series': PickSeries;
    'r2m-proto-pick-book': PickBook;
    'r2m-proto-positions-dialog': PositionsDialog;
  }
}
