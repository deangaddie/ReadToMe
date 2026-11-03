import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { ProjectSummary, SeriesDto } from '@app/api';
import { Router } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import type { NewProjectDialog } from './new-project-dialog';
import type { ProjectsPage } from './projects-page';
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
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** Lets a dialog's close land: happy-dom fires the dialog's close event on a later task. */
async function flush() {
  await settle();
  await new Promise((r) => setTimeout(r, 0));
  await settle();
}

/** Mounts the page over a pending list, checks the skeletons, then answers both reads. */
async function render(projects: ProjectSummary[], series: SeriesDto[] = []) {
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

  it('Move to another series… asks Leave’s confirm, then joins with move', async () => {
    const other: SeriesDto = {
      id: 'x',
      name: 'Holmes novels',
      poster: { url: null, source: 'None', coverPosition: null },
      books: [],
    };
    const page = await render([four, scarlet], [holmes, other]);
    api.on('PUT', '/api/series/x/books/four', { position: 1 });

    card(page, 'four').querySelector<HTMLButtonElement>('[data-action="move-series"]')!.click();
    await settle();
    const picker = document.querySelector('r2m-series-picker')!;
    await picker.rendered();
    picker.querySelector<HTMLButtonElement>('[data-series-id="x"]')!.click();
    await flush();
    const { title } = await confirmDialog('confirm');

    expect(title).toBe('Move The Sign of the Four to Holmes novels?');
    expect(api.calls('PUT', '/api/series/x/books/four').map((r) => r.body)).toEqual([
      { move: true },
    ]);
  });

  it('New series… for a member asks to move first, then creates it and moves the book in', async () => {
    const page = await render([four, scarlet], [holmes]);
    api
      .on('POST', '/api/series', { id: 'n' })
      .on('PUT', '/api/series/n/books/four', { position: 1 });

    card(page, 'four').querySelector<HTMLButtonElement>('[data-action="move-series"]')!.click();
    await settle();
    const picker = document.querySelector('r2m-series-picker')!;
    await picker.rendered();
    picker.querySelector<HTMLButtonElement>('[data-action="new-series"]')!.click();
    await flush();
    const { title } = await confirmDialog('confirm');
    await answerPrompt('Holmes novels');
    await flush();

    expect(title).toBe('Move The Sign of the Four to a new series?');
    expect(api.calls('POST', '/api/series').map((r) => r.body)).toEqual([
      { name: 'Holmes novels', folder: null },
    ]);
    expect(api.calls('PUT', '/api/series/n/books/four').map((r) => r.body)).toEqual([
      { move: true },
    ]);
    expect(toasts).toEqual([
      'The Sign of the Four is Book 1 of Holmes novels. Edit the number if it belongs elsewhere.',
    ]);
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
    await settle();
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

  it('a taken name asks again with the typed name', async () => {
    const page = await render([emma], [holmes]);
    await showSeries(page);
    api.on('PATCH', '/api/series/h', () => problem(409, 'There is already a series called Oz.'));

    page.querySelector<HTMLButtonElement>('[data-action="rename-series"]')!.click();
    await answerPrompt('Oz');

    expect(toasts).toEqual(['problem: There is already a series called Oz.']);
    const prompt = document.querySelector('r2m-text-prompt-dialog')!;
    expect(prompt.querySelector<HTMLInputElement>('.r2m-text-prompt-dialog__input')!.value).toBe(
      'Oz',
    );
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
