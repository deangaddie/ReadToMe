import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { ProjectSummary, SeriesCastDto, SeriesDto } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import type { NewProjectDialog } from './new-project-dialog';
import type { ProjectsPage } from './projects-page';
import type { SeriesVoiceEditor } from './series-voice-editor';
import './projects-page';

const book = (folderName: string, title: string, extra: Partial<ProjectSummary> = {}) =>
  ({
    folderName,
    title,
    author: null,
    coverImage: null,
    audioItemTotal: 0,
    audioItemDone: 0,
    audioPercent: 0,
    series: null,
    fileType: 'Epub',
    ...extra,
  }) satisfies ProjectSummary;

const dracula = book('dracula', 'Dracula', {
  author: 'Bram Stoker',
  coverImage: 'cover.jpg',
  audioItemTotal: 100,
  audioItemDone: 40,
  audioPercent: 40,
});
const emma = book('emma', 'Emma', { fileType: 'Text' });
const scarlet = book('scarlet', 'A Study in Scarlet');
const four = book('four', 'The Sign of the Four');

const holmes: SeriesDto = {
  id: 'h',
  name: 'Sherlock Holmes',
  poster: { url: null, source: 'None', coverPosition: null },
  books: [
    { folder: 'scarlet', position: 1 },
    { folder: 'four', position: 2 },
  ],
};

let api: FakeApi;
let live: FakeLive;
let navigation: FakeNavigation;
let toasts: string[];

beforeEach(() => {
  resetServices();
  localStorage.clear();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  navigation = installNavigation('projects');
  const router = new Router();
  override(Router, router);
  router.start([{ path: 'projects', tag: 'x-nothing' }], async () => true);
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
    warn: (m: string) => toasts.push(`warn: ${m}`),
    error: (m: string) => toasts.push(`error: ${m}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** Lets a dialog's close land: happy-dom fires the dialog's close event on a later task. */
async function flush() {
  await settle();
  await new Promise((r) => setTimeout(r, 0));
  await settle();
}

/** A series' cast board with no Series characters: every book's Characters unlinked. */
function castOf(
  series: SeriesDto,
  projects: ProjectSummary[],
  rows: SeriesCastDto['rows'] = [],
  unlinked: SeriesCastDto['unlinked'] = {},
): SeriesCastDto {
  return {
    books: series.books.map((b) => ({
      folder: b.folder,
      title: projects.find((p) => p.folderName === b.folder)?.title ?? b.folder,
      position: b.position,
    })),
    rows,
    suggestedRows: [],
    unlinked: Object.fromEntries(series.books.map((b) => [b.folder, unlinked[b.folder] ?? []])),
    suggestionCount: 0,
  };
}

/** Mounts the page over a pending list, checks the skeletons, then answers both reads. */
async function render(projects: ProjectSummary[], series: SeriesDto[] = []) {
  for (const s of series) api.on('GET', `/api/series/${s.id}/cast`, castOf(s, projects));
  let answer!: (value: ProjectSummary[]) => void;
  api.on('GET', '/api/projects', () => new Promise<ProjectSummary[]>((r) => (answer = r)));
  api.on('GET', '/api/series', series);
  const page = document.createElement('r2m-projects-page');
  document.body.append(page);
  await page.rendered();
  await settle();
  expect(page.querySelectorAll('.projects__skeleton').length).toBeGreaterThan(0);
  answer(projects);
  api.on('GET', '/api/projects', projects);
  await settle();
  await page.rendered();
  return page;
}

const text = (el: Element | null) => el!.textContent!.replace(/\s+/g, ' ').trim();

const cards = (page: ProjectsPage) =>
  Array.from(page.querySelectorAll('.r2m-project-card'), (c) => c.getAttribute('data-folder'));

/** The Series grid's cards in order: `series:<id>` or the book's folder, and `panel:<id>`. */
const shelf = (page: ProjectsPage) =>
  Array.from(page.querySelector('.projects__grid--series')!.children, (c) =>
    c.classList.contains('projects__series-card')
      ? `series:${c.getAttribute('data-series-id')}`
      : c.classList.contains('projects__panel')
        ? `panel:${c.getAttribute('data-series-id')}`
        : c.getAttribute('data-folder'),
  );

const card = (page: ProjectsPage, folder: string) =>
  page.querySelector<HTMLElement>(`.r2m-project-card[data-folder="${folder}"]`)!;

/** The menu entries of a book card, by label. */
const menuLabels = (el: HTMLElement) =>
  Array.from(el.querySelectorAll('[role=menu] [role=menuitem]'), (item) =>
    text(item.querySelector('span:not(.r2m-icon)')),
  );

async function showSeries(page: ProjectsPage) {
  page.querySelector<HTMLInputElement>('.projects__view input[value="series"]')!.click();
  await page.rendered();
}

async function confirmDialog(choice: 'confirm' | 'cancel') {
  await settle();
  const dialog = document.querySelector('r2m-confirm-dialog')!;
  await dialog.rendered();
  const body = {
    title: text(dialog.querySelector('.r2m-dialog__title')),
    message: text(dialog.querySelector('.r2m-confirm-dialog__message')),
  };
  dialog.querySelector<HTMLButtonElement>(`.r2m-confirm-dialog__${choice}`)!.click();
  await flush();
  return body;
}

async function answerPrompt(value: string) {
  await settle();
  const prompt = document.querySelector('r2m-text-prompt-dialog')!;
  await prompt.rendered();
  const input = prompt.querySelector<HTMLInputElement>('.r2m-text-prompt-dialog__input')!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await prompt.rendered();
  prompt.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__confirm')!.click();
  await flush();
}

describe('r2m-projects-page', () => {
  it('shows skeletons, then a card per project sorted by title', async () => {
    const page = await render([emma, dracula]);
    expect(page.querySelector('.projects__skeleton')).toBeNull();
    expect(cards(page)).toEqual(['dracula', 'emma']);
    const [first, second] = Array.from(page.querySelectorAll('.r2m-project-card'));
    expect(first?.textContent).toContain('Bram Stoker');
    expect(second?.textContent).toContain('Not read in yet');
  });

  it('shows the empty state with the primary action when there are no projects', async () => {
    const page = await render([]);
    expect(page.querySelector('.r2m-empty-state')).not.toBeNull();
    expect(page.querySelector('.r2m-empty-state__action')?.textContent).toContain('New project');
  });

  it('shows the load failure as an error chip, not a toast', async () => {
    api.on('GET', '/api/projects', () => problem(500, 'disk gone'));
    api.on('GET', '/api/series', []);
    const page = document.createElement('r2m-projects-page');
    document.body.append(page);
    await settle();
    await page.rendered();
    expect(page.querySelector('.r2m-status-chip--error')?.textContent).toContain(
      'Projects could not be loaded',
    );
    expect(toasts).toEqual([]);
  });

  it('opens a card by navigating to the overview', async () => {
    const page = await render([dracula]);

    page.querySelector<HTMLButtonElement>('.r2m-project-card__cover')!.click();

    expect(navigation.calls.map((c) => c.url)).toEqual(['http://localhost/app/projects/dracula']);
  });

  it('New project → Create opens the created folder’s overview', async () => {
    const page = await render([dracula]);
    api
      .on('POST', '/api/projects', { folderName: 'Shelf Upload' })
      .on('GET', '/api/projects', [dracula]);

    page.querySelector<HTMLButtonElement>('.projects__new')!.click();
    await settle();
    const dialog = document.querySelector<NewProjectDialog>('r2m-new-project-dialog')!;
    await dialog.rendered();
    for (const [name, value] of [
      ['bookTitle', 'Shelf Upload'],
      ['author', 'U. Loader'],
    ]) {
      const input = dialog.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
      input.value = value!;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    dialog.onFiles([new File(['Chapter 1'], 'shelf-upload.txt')]);
    await dialog.rendered();
    dialog.querySelector<HTMLButtonElement>('.new-project__create')!.click();
    await flush();

    expect(api.calls('POST', '/api/projects')).toHaveLength(1);
    expect(navigation.calls.map((c) => c.url)).toEqual([
      'http://localhost/app/projects/Shelf%20Upload',
    ]);
    expect(toasts).toEqual(['Project created']);
  });

  it('asks for confirmation, then DELETEs and drops the card', async () => {
    const page = await render([dracula, emma]);
    api.on('DELETE', '/api/projects/dracula', undefined).on('GET', '/api/projects', [emma]);

    card(page, 'dracula').querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await settle();
    const dialog = document.querySelector('r2m-confirm-dialog')!;
    await dialog.rendered();
    expect(dialog.textContent).toContain('Delete Dracula?');
    expect(dialog.querySelector('.r2m-confirm-dialog__confirm')?.classList).toContain(
      'r2m-button--danger',
    );
    dialog.querySelector<HTMLButtonElement>('.r2m-confirm-dialog__confirm')!.click();
    await flush();
    await page.rendered();

    expect(api.calls('DELETE', '/api/projects/dracula')).toHaveLength(1);
    expect(cards(page)).toEqual(['emma']);
    expect(toasts).toEqual(['Deleted Dracula']);
  });

  it('a delete refused as busy names the book and the reason, points to Activity, and re-enables the card', async () => {
    const page = await render([dracula]);
    api.on('DELETE', '/api/projects/dracula', () => problem(409, '128 paragraphs · assembling'));

    page.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await confirmDialog('confirm');
    await flush();
    await page.rendered();

    expect(toasts).toEqual([
      "error: Can't delete Dracula: 128 paragraphs · assembling. Stop it in Activity, then delete.",
    ]);
    expect(cards(page)).toEqual(['dracula']);
    expect(page.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.disabled).toBe(
      false,
    );
  });

  it('cancelling the confirm sends nothing', async () => {
    const page = await render([dracula]);

    page.querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await confirmDialog('cancel');
    await page.rendered();

    expect(api.calls('DELETE', '/api/projects/dracula')).toHaveLength(0);
    expect(cards(page)).toEqual(['dracula']);
  });
});

describe('r2m-projects-page Books view', () => {
  it('names a member’s series and number, and offers the series entries', async () => {
    const page = await render([emma, four, scarlet], [holmes]);
    expect(
      text(card(page, 'four').querySelector('.r2m-project-card__series span:not(.r2m-icon)')),
    ).toBe('Sherlock Holmes · Book 2');
    expect(card(page, 'emma').querySelector('.r2m-project-card__series')).toBeNull();
    expect(menuLabels(card(page, 'four'))).toEqual([
      'Open',
      'Move to another series…',
      'Remove from Sherlock Holmes',
      'Delete',
    ]);
    expect(menuLabels(card(page, 'emma'))).toEqual(['Open', 'Add to series…', 'Delete']);
  });

  it('the project delete confirm adds the series line for a member', async () => {
    const page = await render([four, scarlet], [holmes]);
    card(page, 'four').querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    const { message } = await confirmDialog('cancel');
    expect(message).toEndWith(
      'Part of Sherlock Holmes (Book 2); the other books keep their Voices.',
    );
  });

  it('Add to series… joins the picked series and toasts the assigned number', async () => {
    const page = await render([emma, four, scarlet], [holmes]);
    api.on('PUT', '/api/series/h/books/emma', { position: 3 });

    card(page, 'emma').querySelector<HTMLButtonElement>('[data-action="add-to-series"]')!.click();
    await settle();
    const picker = document.querySelector('r2m-series-picker')!;
    await picker.rendered();
    picker.querySelector<HTMLButtonElement>('[data-series-id="h"]')!.click();
    await flush();

    expect(api.calls('PUT', '/api/series/h/books/emma').map((r) => r.body)).toEqual([
      { move: false },
    ]);
    expect(toasts).toEqual([
      'Emma is Book 3 of Sherlock Holmes. Edit the number if it belongs elsewhere.',
    ]);
  });

  it('Add to series… → New series… creates it with the book as Book 1', async () => {
    const page = await render([emma], []);
    api.on('POST', '/api/series', { id: 'n' });

    card(page, 'emma').querySelector<HTMLButtonElement>('[data-action="add-to-series"]')!.click();
    await settle();
    const picker = document.querySelector('r2m-series-picker')!;
    await picker.rendered();
    picker.querySelector<HTMLButtonElement>('[data-action="new-series"]')!.click();
    await flush();
    await answerPrompt('Austen');

    expect(api.calls('POST', '/api/series').map((r) => r.body)).toEqual([
      { name: 'Austen', folder: 'emma' },
    ]);
    expect(toasts).toEqual(['Emma is Book 1 of Austen. Edit the number if it belongs elsewhere.']);
  });

  it('Remove from <series> asks first, then leaves', async () => {
    const page = await render([four, scarlet], [holmes]);
    api.on('DELETE', '/api/series/h/books/four', undefined);

    card(page, 'four').querySelector<HTMLButtonElement>('[data-action="leave-series"]')!.click();
    const { title } = await confirmDialog('confirm');

    expect(title).toBe('Remove The Sign of the Four from Sherlock Holmes?');
    expect(api.calls('DELETE', '/api/series/h/books/four')).toHaveLength(1);
    expect(toasts).toEqual(['Removed The Sign of the Four from Sherlock Holmes']);
  });

  it('reads both lists again on the hub’s series signal', async () => {
    const page = await render([emma, four, scarlet], []);
    api.on('GET', '/api/series', [holmes]);

    live.emit('series', { seriesId: 'h', kind: 'Changed' });
    await settle();
    await page.rendered();

    expect(api.calls('GET', '/api/projects')).toHaveLength(2);
    expect(text(card(page, 'four').querySelector('.r2m-project-card__series'))).toContain(
      'Sherlock Holmes · Book 2',
    );
  });

  it('reads both lists again after a hub reconnect, as a series signal may have been missed', async () => {
    const page = await render([emma, four, scarlet], []);
    api.on('GET', '/api/series', [holmes]);

    live.resync();
    await settle();
    await page.rendered();

    expect(api.calls('GET', '/api/series')).toHaveLength(2);
    expect(text(card(page, 'four').querySelector('.r2m-project-card__series'))).toContain(
      'Sherlock Holmes · Book 2',
    );
  });

  it('a Series write reads the shelf again before it finishes, a hub signal or not', async () => {
    const page = await render([four, scarlet], [holmes]);
    api
      .on('DELETE', '/api/series/h/books/four', undefined)
      .on('GET', '/api/series', [{ ...holmes, books: [holmes.books[0]!] }]);

    card(page, 'four').querySelector<HTMLButtonElement>('[data-action="leave-series"]')!.click();
    await confirmDialog('confirm');
    await page.rendered();

    expect(api.calls('GET', '/api/series')).toHaveLength(2);
    expect(card(page, 'four').querySelector('.r2m-project-card__series')).toBeNull();
  });

  it('deleting a member book reads the series again', async () => {
    const page = await render([four, scarlet], [holmes]);
    api.on('DELETE', '/api/projects/four', undefined).on('GET', '/api/projects', [scarlet]);

    card(page, 'four').querySelector<HTMLButtonElement>('.r2m-project-card__delete')!.click();
    await confirmDialog('confirm');

    expect(api.calls('GET', '/api/series')).toHaveLength(2);
  });
});

describe('r2m-projects-page Series view', () => {
  it('is remembered per browser', async () => {
    const page = await render([emma], []);
    await showSeries(page);
    expect(localStorage.getItem('r2m.projects.view')).toBe('series');
    page.remove();

    const again = document.createElement('r2m-projects-page');
    document.body.append(again);
    await settle();
    await again.rendered();
    expect(again.querySelector('.projects__grid--series')).not.toBeNull();
    expect(again.querySelector('.projects__new-series')).not.toBeNull();
  });

  it('puts series cards among the books in no series, by name', async () => {
    const page = await render([emma, dracula, four, scarlet], [holmes]);
    await showSeries(page);
    expect(shelf(page)).toEqual(['dracula', 'emma', 'series:h']);
    expect(text(page.querySelector('.projects__series-summary'))).toBe('2 books · Books 1–2');
  });

  it('with no series, shows the empty state with New series over the books', async () => {
    const page = await render([emma], []);
    await showSeries(page);
    expect(text(page.querySelector('.r2m-empty-state'))).toContain('No series yet');
    expect(text(page.querySelector('.r2m-empty-state__action'))).toContain('New series');
    expect(shelf(page)).toEqual(['emma']);
  });

  it('clicking a series card opens its panel with the books in number order', async () => {
    const page = await render([emma, four, scarlet], [holmes]);
    await showSeries(page);
    page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
    await page.rendered();

    // One column in happy-dom: the panel follows the clicked card.
    expect(shelf(page)).toEqual(['emma', 'series:h', 'panel:h']);
    const panel = page.querySelector('.projects__panel')!;
    expect(
      Array.from(panel.querySelectorAll('.r2m-project-card'), (c) => [
        c.getAttribute('data-folder'),
        text(c.querySelector('.r2m-project-card__badge')),
      ]),
    ).toEqual([
      ['scarlet', 'Book 1'],
      ['four', 'Book 2'],
    ]);

    page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
    await page.rendered();
    expect(page.querySelector('.projects__panel')).toBeNull();
  });

  it('Edit book numbers saves the set, and Save is off on a tie or when unchanged', async () => {
    const page = await render([four, scarlet], [holmes]);
    await showSeries(page);
    page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
    await page.rendered();
    page.querySelector<HTMLButtonElement>('[data-action="edit-numbers"]')!.click();
    await page.rendered();

    const save = () => page.querySelector<HTMLButtonElement>('[data-action="save-numbers"]')!;
    const typeIn = async (folder: string, value: string) => {
      const input = page.querySelector<HTMLInputElement>(`input[data-folder="${folder}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      await page.rendered();
    };
    expect(save().disabled).toBe(true);

    await typeIn('scarlet', '2');
    expect(save().disabled).toBe(true);
    expect(text(page.querySelector('.projects__panel-problem'))).toContain(
      'A Study in Scarlet and The Sign of the Four are both Book 2.',
    );

    await typeIn('four', '1');
    expect(save().disabled).toBe(false);
    api.on('PUT', '/api/series/h/positions', holmes);
    save().click();
    await flush();
    await page.rendered();

    expect(api.calls('PUT', '/api/series/h/positions').map((r) => r.body)).toEqual([
      { positions: { scarlet: 2, four: 1 } },
    ]);
    expect(page.querySelector('[data-action="save-numbers"]')).toBeNull();
  });

  it('a re-read while editing follows the members and keeps what was typed', async () => {
    const page = await render([four, scarlet, emma], [holmes]);
    await showSeries(page);
    page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
    await page.rendered();
    page.querySelector<HTMLButtonElement>('[data-action="edit-numbers"]')!.click();
    await page.rendered();
    const input = page.querySelector<HTMLInputElement>('input[data-folder="four"]')!;
    input.value = '7';
    input.dispatchEvent(new Event('input'));
    await page.rendered();

    api.on('GET', '/api/series', [
      { ...holmes, books: [...holmes.books, { folder: 'emma', position: 3 }] },
    ]);
    live.emit('series', { seriesId: 'h', kind: 'Changed' });
    await settle();
    await page.rendered();

    const boxes = Array.from(
      page.querySelectorAll<HTMLInputElement>('.projects__panel input[data-folder]'),
      (i) => [i.getAttribute('data-folder'), i.value],
    );
    expect(boxes).toEqual([
      ['scarlet', '1'],
      ['four', '7'],
      ['emma', '3'],
    ]);
  });

  it('New series asks for a name and creates an empty series', async () => {
    const page = await render([emma], []);
    await showSeries(page);
    api.on('POST', '/api/series', { id: 'n' });

    page.querySelector<HTMLButtonElement>('.projects__new-series')!.click();
    await answerPrompt('Austen');

    expect(api.calls('POST', '/api/series').map((r) => r.body)).toEqual([
      { name: 'Austen', folder: null },
    ]);
  });

  it('Add book… groups the other books and joins the picked one', async () => {
    const page = await render([emma, four, scarlet], [holmes]);
    await showSeries(page);
    api.on('PUT', '/api/series/h/books/emma', { position: 3 });

    page.querySelector<HTMLButtonElement>('[data-action="add-book"]')!.click();
    await settle();
    const picker = document.querySelector('r2m-book-picker')!;
    await picker.rendered();
    expect(
      Array.from(picker.querySelectorAll('[data-folder]'), (r) => r.getAttribute('data-folder')),
    ).toEqual(['emma']);
    picker.querySelector<HTMLButtonElement>('[data-folder="emma"]')!.click();
    await flush();

    expect(api.calls('PUT', '/api/series/h/books/emma').map((r) => r.body)).toEqual([
      { move: false },
    ]);
    expect(toasts).toEqual([
      'Emma is Book 3 of Sherlock Holmes. Edit the number if it belongs elsewhere.',
    ]);
  });

  it('Delete series names the books in a red confirm, then deletes', async () => {
    const page = await render([four, scarlet], [holmes]);
    await showSeries(page);
    api.on('DELETE', '/api/series/h', undefined).on('GET', '/api/series', []);

    page.querySelector<HTMLButtonElement>('[data-action="delete-series"]')!.click();
    const { title, message } = await confirmDialog('confirm');

    expect(title).toBe('Delete series Sherlock Holmes?');
    expect(message).toContain('A Study in Scarlet and The Sign of the Four');
    expect(api.calls('DELETE', '/api/series/h')).toHaveLength(1);
    expect(toasts).toEqual(['Deleted series Sherlock Holmes']);
  });
});

describe('r2m-projects-page series poster', () => {
  const posterOf = (page: ProjectsPage) => page.querySelector<HTMLElement>('.projects__poster')!;
  const withPoster = (poster: SeriesDto['poster']): SeriesDto => ({ ...holmes, poster });
  const uploaded = withPoster({
    url: '/api/series/h/poster?v=7',
    source: 'Uploaded',
    coverPosition: null,
  });

  /** Picks `file` in the card's hidden poster input, as the browser's file dialog would. */
  async function pick(page: ProjectsPage, file: File) {
    const input = page.querySelector<HTMLInputElement>('.projects__poster-input')!;
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    await flush();
  }

  it('shows the uploaded poster, with Replace poster… and Remove poster in the menu', async () => {
    const page = await render([four, scarlet], [uploaded]);
    await showSeries(page);

    const poster = posterOf(page);
    expect(poster.dataset.poster).toBe('Uploaded');
    expect(poster.querySelector('img')!.getAttribute('src')).toBe('/api/series/h/poster?v=7');
    expect(poster.querySelector('.projects__poster-note')).toBeNull();
    const menu = page.querySelector<HTMLElement>('.projects__series-card')!;
    expect(menuLabels(menu)).toEqual([
      'Add book…',
      'Rename…',
      'Replace poster…',
      'Remove poster',
      'Delete series',
    ]);
  });

  it('marks a fallback cover "cover of Book N", with Upload poster… and no Remove', async () => {
    const page = await render(
      [four, scarlet],
      [withPoster({ url: '/api/series/h/poster?v=3', source: 'Cover', coverPosition: 1.5 })],
    );
    await showSeries(page);

    const poster = posterOf(page);
    expect(poster.dataset.poster).toBe('Cover');
    expect(poster.querySelector('img')!.getAttribute('src')).toBe('/api/series/h/poster?v=3');
    expect(text(poster.querySelector('.projects__poster-note'))).toBe('cover of Book 1.5');
    const menu = page.querySelector<HTMLElement>('.projects__series-card')!;
    expect(menuLabels(menu)).toEqual(['Add book…', 'Rename…', 'Upload poster…', 'Delete series']);
  });

  it('with no image, shows the striped placeholder with the series name', async () => {
    const page = await render([four, scarlet], [holmes]);
    await showSeries(page);

    const poster = posterOf(page);
    expect(poster.dataset.poster).toBe('None');
    expect(poster.classList.contains('projects__poster--empty')).toBe(true);
    expect(poster.querySelector('img')).toBeNull();
    expect(text(poster.querySelector('.projects__poster-name'))).toBe('Sherlock Holmes');
  });

  it('a picked image uploads with no confirm and the series is read again', async () => {
    const page = await render([four, scarlet], [holmes]);
    await showSeries(page);
    api.on('PUT', '/api/series/h/poster', uploaded).on('GET', '/api/series', [uploaded]);

    await pick(page, new File(['png'], 'Holmes.PNG', { type: 'image/png' }));
    await page.rendered();

    expect(document.querySelector('r2m-confirm-dialog')).toBeNull();
    const [put] = api.calls('PUT', '/api/series/h/poster');
    expect((put!.body as FormData).get('file')).toBeInstanceOf(File);
    expect(posterOf(page).dataset.poster).toBe('Uploaded');
  });

  it('turns away a file outside the cover’s rules before sending it', async () => {
    const page = await render([four, scarlet], [holmes]);
    await showSeries(page);

    await pick(page, new File(['gif'], 'holmes.gif', { type: 'image/gif' }));
    await pick(page, new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'big.jpg'));

    expect(api.calls('PUT', '/api/series/h/poster')).toHaveLength(0);
    expect(toasts).toEqual([
      'warn: Use a .jpg, .png or .webp image.',
      'warn: Poster image must be 10 MB or smaller.',
    ]);
  });

  it('Remove poster asks in red, naming the cover that shows instead, then clears it', async () => {
    const coveredScarlet = { ...scarlet, coverImage: 'cover.jpg' };
    const page = await render([four, coveredScarlet], [uploaded]);
    await showSeries(page);
    api.on('DELETE', '/api/series/h/poster', undefined).on('GET', '/api/series', [holmes]);

    page.querySelector<HTMLButtonElement>('[data-action="remove-poster"]')!.click();
    const { title, message } = await confirmDialog('confirm');

    expect(title).toBe('Remove the poster of Sherlock Holmes?');
    expect(message).toContain('no History');
    expect(message).toContain('The series shows the cover of Book 1 instead.');
    expect(api.calls('DELETE', '/api/series/h/poster')).toHaveLength(1);
    await page.rendered();
    expect(posterOf(page).dataset.poster).toBe('None');
  });

  it('Remove poster cancelled leaves the poster alone', async () => {
    const page = await render([four, scarlet], [uploaded]);
    await showSeries(page);

    page.querySelector<HTMLButtonElement>('[data-action="remove-poster"]')!.click();
    const { message } = await confirmDialog('cancel');

    expect(message).toContain('a placeholder with its name');
    expect(api.calls('DELETE', '/api/series/h/poster')).toHaveLength(0);
  });

  it('Delete series names an uploaded poster among what goes', async () => {
    const page = await render([four, scarlet], [uploaded]);
    await showSeries(page);

    page.querySelector<HTMLButtonElement>('[data-action="delete-series"]')!.click();
    const { message } = await confirmDialog('cancel');

    expect(message).toContain('name, poster, book numbers and links are removed');
  });
});

describe('r2m-projects-page series cast board', () => {
  const linked = (characterId: string, name: string) => ({ characterId, name, differs: false });
  const holmesRow = {
    seriesCharacterId: 'sc-holmes',
    label: 'Sherlock Holmes',
    isNarrator: false,
    cells: [
      { folder: 'scarlet', linked: linked('s-holmes', 'Sherlock Holmes'), suggested: null },
      { folder: 'four', linked: null, suggested: null },
    ],
  };
  const narratorRow = {
    seriesCharacterId: 'sc-narrator',
    label: 'Narrator',
    isNarrator: true,
    cells: [
      { folder: 'scarlet', linked: linked('s-narrator', 'Narrator'), suggested: null },
      { folder: 'four', linked: linked('f-narrator', 'Narrator'), suggested: null },
    ],
  };
  const unlinked = {
    scarlet: [{ characterId: 's-watson', name: 'Dr. Watson', isNarrator: false, nameMatches: [] }],
    four: [
      { characterId: 'f-holmes', name: 'Holmes', isNarrator: false, nameMatches: [] },
      { characterId: 'f-watson', name: 'John Watson', isNarrator: false, nameMatches: [] },
    ],
  };
  const board = () => castOf(holmes, [scarlet, four], [narratorRow, holmesRow], unlinked);

  /** Opens the Holmes panel's Characters tab over `cast`. */
  async function openBoard(cast = board()) {
    const page = await render([four, scarlet], [holmes]);
    api.on('GET', '/api/series/h/cast', cast);
    await showSeries(page);
    page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
    await page.rendered();
    page.querySelector<HTMLButtonElement>('[role=tab][data-tab="characters"]')!.click();
    await page.rendered();
    await settle();
    const el = page.querySelector('r2m-series-cast-board')!;
    await el.rendered();
    return { page, el };
  }

  const cellOf = (el: Element, row: string, folder: string) =>
    el.querySelector<HTMLElement>(
      `tr[data-series-character="${row}"] td[data-folder="${folder}"]`,
    )!;

  async function pick(select: HTMLSelectElement, value: string) {
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await flush();
  }

  it('shows a row per Series character and a column per book, the Narrator a row too', async () => {
    const { el } = await openBoard();

    expect(Array.from(el.querySelectorAll('thead th'), text)).toEqual([
      'Series character',
      'Book 1A Study in Scarlet',
      'Book 2The Sign of the Four',
    ]);
    expect(Array.from(el.querySelectorAll('tbody th[scope=row]'), text)).toEqual([
      'expand_more record_voice_over Narrator',
      'expand_more person Sherlock Holmes',
      'person_add Share as new',
    ]);
    expect(cellOf(el, 'sc-holmes', 'scarlet').dataset.cell).toBe('linked');
    expect(text(cellOf(el, 'sc-holmes', 'scarlet'))).toContain('Sherlock Holmes');
    expect(cellOf(el, 'sc-holmes', 'four').dataset.cell).toBe('empty');
  });

  it('a linked cell whose Voices differ from series carries the differs badge (#222)', async () => {
    const differing = {
      ...narratorRow,
      cells: [
        narratorRow.cells[0]!,
        {
          folder: 'four',
          linked: { ...linked('f-narrator', 'Narrator'), differs: true },
          suggested: null,
        },
      ],
    };
    const { el } = await openBoard(
      castOf(holmes, [scarlet, four], [differing, holmesRow], unlinked),
    );

    expect(cellOf(el, 'sc-narrator', 'scarlet').querySelector('.series-cast__differs')).toBeNull();
    expect(
      cellOf(el, 'sc-narrator', 'four')
        .querySelector('.series-cast__differs')
        ?.getAttribute('data-tooltip'),
    ).toBe('Differs from series');
  });

  it('an empty cell picks among that book’s unlinked Characters', async () => {
    const { el } = await openBoard();
    const options = Array.from(cellOf(el, 'sc-holmes', 'four').querySelectorAll('option'), text);
    expect(options).toEqual(['Link…', 'Holmes', 'John Watson']);
  });

  it('Link asks with what each side gains, then links and reads the board again', async () => {
    const { el } = await openBoard();
    api
      .on('GET', '/api/series/h/characters/sc-holmes/links/four/preview', {
        bookGains: ['Deerstalker'],
        seriesGains: [],
      })
      .on('PUT', '/api/series/h/characters/sc-holmes/links/four', undefined);

    await pick(cellOf(el, 'sc-holmes', 'four').querySelector('select')!, 'f-holmes');
    const { title, message } = await confirmDialog('confirm');

    expect(title).toBe('Link Holmes to Sherlock Holmes?');
    expect(message).toContain(
      'Holmes will share Voices with Sherlock Holmes in A Study in Scarlet.',
    );
    expect(message).toContain(
      'The Sign of the Four gains “Deerstalker”; the other book gains no Voices.',
    );
    const preview = api.calls('GET', '/api/series/h/characters/sc-holmes/links/four/preview');
    expect(preview[0]!.search).toBe('?characterId=f-holmes');
    const put = api.calls('PUT', '/api/series/h/characters/sc-holmes/links/four');
    expect(put.map((r) => r.body)).toEqual([{ characterId: 'f-holmes' }]);
    // The panel's suggestion count on opening, the board's read, and its read after the link.
    expect(api.calls('GET', '/api/series/h/cast')).toHaveLength(3);
    expect(toasts).toEqual(['Holmes is linked to Sherlock Holmes']);
  });

  it('Link cancelled writes nothing', async () => {
    const { el } = await openBoard();
    api.on('GET', '/api/series/h/characters/sc-holmes/links/four/preview', {
      bookGains: [],
      seriesGains: [],
    });

    await pick(cellOf(el, 'sc-holmes', 'four').querySelector('select')!, 'f-holmes');
    await confirmDialog('cancel');

    expect(api.calls('PUT', '/api/series/h/characters/sc-holmes/links/four')).toHaveLength(0);
  });

  it('✕ unlinks after a confirm saying the book keeps its copy', async () => {
    const { el } = await openBoard();
    api.on('DELETE', '/api/series/h/characters/sc-narrator/links/four', undefined);

    cellOf(el, 'sc-narrator', 'four')
      .querySelector<HTMLButtonElement>('[data-action="unlink"]')!
      .click();
    const { title, message } = await confirmDialog('confirm');

    expect(title).toBe('Unlink Narrator from Narrator?');
    expect(message).toContain('The Sign of the Four keeps its own copy of Narrator’s Voices');
    expect(message).toContain('no longer reach A Study in Scarlet');
    expect(api.calls('DELETE', '/api/series/h/characters/sc-narrator/links/four')).toHaveLength(1);
  });

  it('Share as new picks a Character of a book, asks, then shares it', async () => {
    const { el } = await openBoard();
    api.on('POST', '/api/series/h/characters', { seriesCharacterId: 'sc-watson' });

    const share = el.querySelector<HTMLSelectElement>(
      'td[data-cell="share"][data-folder="scarlet"] select',
    )!;
    await pick(share, 's-watson');
    const { title } = await confirmDialog('confirm');

    expect(title).toBe('Share Dr. Watson across Sherlock Holmes?');
    expect(api.calls('POST', '/api/series/h/characters').map((r) => r.body)).toEqual([
      { folder: 'scarlet', characterId: 's-watson' },
    ]);
    expect(toasts).toEqual(['Dr. Watson is shared across Sherlock Holmes']);
  });

  it('reads the board again on the hub’s series signal for its own series', async () => {
    const { el } = await openBoard();

    live.emit('series', { seriesId: 'other', kind: 'Changed' });
    live.emit('series', { seriesId: 'h', kind: 'Changed' });
    await settle();
    await el.rendered();

    // The panel's suggestion count and the board's read on opening, then one for this series'
    // signal (the board's: the panel leaves it to the showing board); another series' reads nothing.
    expect(api.calls('GET', '/api/series/h/cast')).toHaveLength(3);
  });

  it('reads the board again after a hub reconnect', async () => {
    const { el } = await openBoard();

    live.resync();
    await settle();
    await el.rendered();

    // Opening's two reads, then the board's after the reconnect.
    expect(api.calls('GET', '/api/series/h/cast')).toHaveLength(3);
  });

  describe('Link suggestions (#223)', () => {
    /** Four's Holmes suggested in Holmes' row, and Watson a suggested row across both books. */
    const suggesting = (): SeriesCastDto => ({
      ...castOf(
        holmes,
        [scarlet, four],
        [
          narratorRow,
          {
            ...holmesRow,
            cells: [
              holmesRow.cells[0]!,
              {
                folder: 'four',
                linked: null,
                suggested: { characterId: 'f-holmes', name: 'Holmes' },
              },
            ],
          },
        ],
        {
          scarlet: unlinked.scarlet,
          four: [{ ...unlinked.four[0]!, nameMatches: ['sc-holmes'] }, unlinked.four[1]!],
        },
      ),
      suggestedRows: [
        {
          label: 'Dr. Watson',
          members: [
            { folder: 'scarlet', characterId: 's-watson', name: 'Dr. Watson' },
            { folder: 'four', characterId: 'f-watson', name: 'John Watson' },
          ],
        },
      ],
      suggestionCount: 2,
    });

    const watsonMembers = [
      { folder: 'scarlet', characterId: 's-watson' },
      { folder: 'four', characterId: 'f-watson' },
    ];

    const suggestedRow = (el: Element) =>
      el.querySelector<HTMLElement>('tr[data-suggested-row="s-watson f-watson"]')!;

    it('a suggested cell shows the Character with Link and ✕', async () => {
      const { el } = await openBoard(suggesting());
      const cell = cellOf(el, 'sc-holmes', 'four');

      expect(cell.dataset.cell).toBe('suggested');
      expect(text(cell)).toBe('Holmes? Link close');
      expect(cell.querySelector('[data-action="not-the-same"]')!.getAttribute('aria-label')).toBe(
        'Holmes is not Sherlock Holmes',
      );
    });

    it('a suggested cell’s Link runs the Link confirm and links', async () => {
      const { el } = await openBoard(suggesting());
      api
        .on('GET', '/api/series/h/characters/sc-holmes/links/four/preview', {
          bookGains: [],
          seriesGains: [],
        })
        .on('PUT', '/api/series/h/characters/sc-holmes/links/four', undefined);

      cellOf(el, 'sc-holmes', 'four')
        .querySelector<HTMLButtonElement>('[data-action="link-suggested"]')!
        .click();
      const { title } = await confirmDialog('confirm');

      expect(title).toBe('Link Holmes to Sherlock Holmes?');
      expect(
        api.calls('PUT', '/api/series/h/characters/sc-holmes/links/four').map((r) => r.body),
      ).toEqual([{ characterId: 'f-holmes' }]);
    });

    it('a suggested cell’s ✕ records Not the same without a confirm, then reads again', async () => {
      const { el } = await openBoard(suggesting());
      api.on('POST', '/api/series/h/not-the-same', undefined);

      cellOf(el, 'sc-holmes', 'four')
        .querySelector<HTMLButtonElement>('[data-action="not-the-same"]')!
        .click();
      await flush();

      expect(document.querySelector('r2m-confirm-dialog')).toBeNull();
      expect(api.calls('POST', '/api/series/h/not-the-same').map((r) => r.body)).toEqual([
        { seriesCharacterId: 'sc-holmes', folder: 'four', characterId: 'f-holmes' },
      ]);
      // The panel's count and the board's read on opening, then the board's read after the write.
      expect(api.calls('GET', '/api/series/h/cast')).toHaveLength(3);
    });

    it('a suggested row shows each book’s Character, with Share and link and ✕', async () => {
      const { el } = await openBoard(suggesting());
      const row = suggestedRow(el);

      expect(text(row.querySelector('th'))).toBe('Dr. Watson? Share and link close');
      expect(Array.from(row.querySelectorAll('td'), text)).toEqual(['Dr. Watson?', 'John Watson?']);
      // Suggested rows come after the Series characters, before Share as new.
      expect(Array.from(el.querySelectorAll('tbody > tr'), (r) => r.className)).toEqual([
        '',
        '',
        'series-cast__suggested-row',
        'series-cast__share-row',
      ]);
    });

    it('Share and link asks once, naming what each book gains, then shares and links', async () => {
      const { el } = await openBoard(suggesting());
      api
        .on('POST', '/api/series/h/characters/share-and-link/preview', {
          books: [
            { folder: 'scarlet', title: 'A Study in Scarlet', gains: ['Brisk'] },
            { folder: 'four', title: 'The Sign of the Four', gains: [] },
          ],
        })
        .on('POST', '/api/series/h/characters/share-and-link', { seriesCharacterId: 'sc-watson' });

      suggestedRow(el).querySelector<HTMLButtonElement>('[data-action="share-and-link"]')!.click();
      const { title, message } = await confirmDialog('confirm');

      expect(title).toBe('Share and link Dr. Watson across Sherlock Holmes?');
      expect(message).toContain(
        'Dr. Watson in A Study in Scarlet and John Watson in The Sign of the Four become one ' +
          'series character',
      );
      expect(message).toContain(
        'A Study in Scarlet gains “Brisk”; The Sign of the Four gains no Voices.',
      );
      expect(
        api.calls('POST', '/api/series/h/characters/share-and-link/preview').map((r) => r.body),
      ).toEqual([{ members: watsonMembers }]);
      expect(
        api.calls('POST', '/api/series/h/characters/share-and-link').map((r) => r.body),
      ).toEqual([{ members: watsonMembers }]);
      expect(toasts).toEqual(['Dr. Watson is shared and linked across Sherlock Holmes']);
    });

    it('Share and link cancelled writes nothing', async () => {
      const { el } = await openBoard(suggesting());
      api.on('POST', '/api/series/h/characters/share-and-link/preview', { books: [] });

      suggestedRow(el).querySelector<HTMLButtonElement>('[data-action="share-and-link"]')!.click();
      await confirmDialog('cancel');

      expect(api.calls('POST', '/api/series/h/characters/share-and-link')).toHaveLength(0);
    });

    it('a suggested row’s ✕ records each pair as not the same', async () => {
      const { el } = await openBoard(suggesting());
      api.on('POST', '/api/series/h/not-the-same', undefined);

      suggestedRow(el)
        .querySelector<HTMLButtonElement>('[data-action="not-the-same-row"]')!
        .click();
      await flush();

      expect(api.calls('POST', '/api/series/h/not-the-same').map((r) => r.body)).toEqual([
        { members: watsonMembers },
      ]);
    });

    it('an ambiguous cell’s picker lists the name matches first, marked', async () => {
      const character = (characterId: string, name: string, nameMatches: string[] = []) => ({
        characterId,
        name,
        isNarrator: false,
        nameMatches,
      });
      const { el } = await openBoard(
        castOf(holmes, [scarlet, four], [narratorRow, holmesRow], {
          scarlet: unlinked.scarlet,
          four: [
            character('f-watson', 'John Watson'),
            character('f-holmes', 'Holmes', ['sc-holmes']),
            character('f-sherlock', 'Sherlock', ['sc-holmes']),
          ],
        }),
      );

      expect(Array.from(cellOf(el, 'sc-holmes', 'four').querySelectorAll('option'), text)).toEqual([
        'Link…',
        'Holmes — name matches',
        'Sherlock — name matches',
        'John Watson',
      ]);
      // The Share as new picker marks nothing.
      expect(
        Array.from(el.querySelectorAll('td[data-cell="share"][data-folder="four"] option'), text),
      ).toEqual(['Share…', 'John Watson', 'Holmes', 'Sherlock']);
    });

    it('the Characters tab counts suggestions, from opening the panel and then from the board', async () => {
      const page = await render([four, scarlet], [holmes]);
      api.on('GET', '/api/series/h/cast', suggesting());
      await showSeries(page);
      page.querySelector<HTMLButtonElement>('.projects__series-open')!.click();
      await flush();
      await page.rendered();
      const tab = () => text(page.querySelector('[role=tab][data-tab="characters"]'));

      expect(tab()).toBe('groupsCharacters · 2 suggestions');

      api.on('GET', '/api/series/h/cast', {
        ...suggesting(),
        suggestedRows: [],
        suggestionCount: 1,
      });
      page.querySelector<HTMLButtonElement>('[role=tab][data-tab="characters"]')!.click();
      await flush();
      await page.rendered();

      expect(tab()).toBe('groupsCharacters · 1 suggestion');
    });
  });

  describe('inline series Voice editors (#221)', () => {
    const detailOf = (label: string) => ({
      seriesCopyFolder: 'scarlet',
      voices: [],
      books: [
        {
          folder: 'scarlet',
          title: 'A Study in Scarlet',
          position: 1,
          characterId: `s-${label}`,
          characterName: label,
          defaultVoiceId: null,
          differs: false,
        },
      ],
      deleteBlocks: {},
    });

    function routeEditors() {
      for (const id of ['sc-narrator', 'sc-holmes'])
        api.on('GET', `/api/series/h/characters/${id}`, detailOf(id));
      for (const path of ['summary']) api.on('GET', `/api/projects/scarlet/characters/${path}`, []);
    }

    const toggle = (el: Element, row: string) =>
      el.querySelector<HTMLButtonElement>(
        `tr[data-series-character="${row}"] [data-action=voices]`,
      )!;

    it('a row’s label opens its editor directly under it, several at once, and closes it again', async () => {
      routeEditors();
      const { el } = await openBoard();

      toggle(el, 'sc-narrator').click();
      await el.rendered();
      toggle(el, 'sc-holmes').click();
      await el.rendered();
      await settle();

      const rows = Array.from(
        el.querySelectorAll(':scope tbody > tr'),
        (tr) =>
          tr.getAttribute('data-series-character') ??
          `editor:${tr.getAttribute('data-editor-for')}`,
      );
      expect(rows.slice(0, 4)).toEqual([
        'sc-narrator',
        'editor:sc-narrator',
        'sc-holmes',
        'editor:sc-holmes',
      ]);
      expect(toggle(el, 'sc-narrator').getAttribute('aria-expanded')).toBe('true');
      const editor = el.querySelector<SeriesVoiceEditor>(
        'tr[data-editor-for="sc-holmes"] r2m-series-voice-editor',
      )!;
      expect(editor.seriesId).toBe('h');
      expect(editor.label).toBe('Sherlock Holmes');

      toggle(el, 'sc-narrator').click();
      await el.rendered();
      expect(el.querySelector('tr[data-editor-for="sc-narrator"]')).toBeNull();
      expect(el.querySelector('tr[data-editor-for="sc-holmes"]')).not.toBeNull();
    });

    it('arriving from the Cast page opens the series’ Characters tab with that row’s editor open', async () => {
      routeEditors();
      api.on('GET', '/api/series/h/cast', board());
      navigation = installNavigation('projects?series=h&character=sc-holmes');
      const router = new Router();
      override(Router, router);
      router.start([{ path: 'projects', tag: 'x-nothing' }], async () => true);

      const page = await render([four, scarlet], [holmes]);
      api.on('GET', '/api/series/h/cast', board());
      await settle();
      await page.rendered();
      const el = page.querySelector('r2m-series-cast-board')!;
      await el.rendered();
      await el.load(); // render() routed an empty cast before this one
      await settle();
      await el.rendered();

      expect(
        page.querySelector('[role=tab][data-tab="characters"]')?.getAttribute('aria-selected'),
      ).toBe('true');
      expect(
        el.querySelector('tr[data-editor-for="sc-holmes"] r2m-series-voice-editor'),
      ).not.toBeNull();
      expect(el.querySelector('tr[data-editor-for="sc-narrator"]')).toBeNull();
    });
  });
});
