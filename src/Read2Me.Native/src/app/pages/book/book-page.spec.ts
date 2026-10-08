import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { BookOverviewDto, ParagraphDto } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import { BookStore } from './book-store';
import type { BookPage } from './book-page';
import '../project/project-shell';
import './book-page';

const BASE = '/api/projects/dune';

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [{ id: 'h', name: 'Hardin', aliases: [] }],
  totalParts: 1,
  totalChapters: 2,
};

const DIALOG = (id: string): ParagraphDto => ({
  id,
  isPauseParagraph: false,
  items: [
    {
      id: `${id}-i`,
      itemType: 'Character',
      text: `"${id}"`,
      characterId: null,
      audioFileName: null,
      voiceInstructions: null,
      orderKey: 'a',
      isPause: false,
    },
  ],
});

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: 'book', tag: 'r2m-book-page' }],
  },
];

let api: FakeApi;
let navigation: FakeNavigation;

/** The book page under the project shell, through a root outlet as in the app. */
async function start(path: string) {
  navigation = installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<BookPage>('r2m-book-page');
  if (!page) throw new Error('the book page did not render');
  await page.rendered();
  return { page, store: use(BookStore, page), router };
}

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  new FakeLive().install();
  api
    .on('GET', BASE, { folderName: 'dune', title: 'Dune', narrator: null, narratorOnlyMode: false })
    .on('GET', `${BASE}/status`, { revision: 1, nodes: {}, audio: { remaining: 0 } })
    .on('GET', `${BASE}/book`, OVERVIEW)
    .on('GET', `${BASE}/audio/reviews`, {})
    .on('GET', `${BASE}/nodes/volume/v1/children`, { parts: [{ id: 'p1', title: null }] })
    .on('GET', `${BASE}/nodes/part/p1/children`, {
      chapters: [
        { id: 'c1', title: 'The Encyclopedists' },
        { id: 'c2', title: null },
      ],
    })
    .on('GET', `${BASE}/nodes/chapter/c1/children`, { paragraphs: [DIALOG('p1'), DIALOG('p2')] })
    .on('GET', `${BASE}/nodes/chapter/c2/children`, { paragraphs: [DIALOG('p3')] })
    .on('GET', `${BASE}/nodes/chapter/c1/voices`, {})
    .on('GET', `${BASE}/nodes/chapter/c2/voices`, {});
});
afterEach(() => document.body.replaceChildren());

const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('r2m-book-page', () => {
  it('shows an empty state when the book has not been read in', async () => {
    api.on('GET', `${BASE}/book`, { ...OVERVIEW, hasContent: false, volumes: [] });
    const { page } = await start('projects/dune/book');
    expect(page.textContent).toContain('The book has not been read in yet');
    expect(page.querySelector('r2m-measured-list')).toBeNull();
    expect(page.querySelector('a.r2m-button')?.getAttribute('href')).toBe('./');
  });

  it('opens the first chapter and renders its header and paragraph rows in the list', async () => {
    const { page, store } = await start('projects/dune/book');
    // happy-dom gives the list no height, so the edge check (as in a real short viewport) has
    // already asked for the next chapter: the window starts with c1 and may hold c2 too.
    expect(store.window()[0]).toBe('c1');
    expect(text(page.querySelector('.book__chapter'))).toBe('The Encyclopedists');
    const rows = page.querySelectorAll('.r2m-vlist__row');
    expect(Array.from(rows, (r) => r.getAttribute('data-row-key')).slice(0, 3)).toEqual([
      'chapter:c1',
      'paragraph:p1',
      'paragraph:p2',
    ]);
    expect(page.querySelectorAll('.r2m-paragraph[data-chapter-id=c1]').length).toBe(2);
    expect(page.querySelector('.book__skeleton')).toBeNull();
    // The current chapter follows the list's top row.
    expect(page.getAttribute('data-current-chapter')).toBe('c1');
  });

  it('lists the chapters in the tree with their badges, and a tree click opens a chapter', async () => {
    api.on('GET', `${BASE}/status`, {
      revision: 1,
      nodes: {
        c1: {
          attributionRemaining: 3,
          audioRemaining: 0,
          review: 0,
          attributionProcessing: false,
          attributionQueued: 0,
          isDone: false,
        },
      },
      audio: { remaining: 0 },
    });
    const { page, store } = await start('projects/dune/book');
    const tree = page.querySelector('nav.book__tree r2m-structure-tree')!;
    const titles = () =>
      Array.from(tree.querySelectorAll('.tree__title')).map((n) => n.textContent?.trim());
    expect(titles()).toEqual(['The Encyclopedists', 'Chapter 2']);
    expect(tree.querySelector('[data-node-id=c1] .r2m-count-badge')?.textContent).toContain('3');
    expect(tree.querySelector('[data-node-id=c1]')?.getAttribute('aria-current')).toBe('true');
    expect(store.window()[0]).toBe('c1');
    // One shared menu element serves every trigger on the page.
    expect(page.querySelectorAll('r2m-node-menu').length).toBe(1);
    expect(tree.querySelectorAll('.r2m-node-menu__trigger').length).toBe(2);

    tree.querySelector<HTMLElement>('[data-node-id=c2] .tree__title')!.click();
    for (let i = 0; i < 4; i++) await settle();
    expect(store.currentChapterId()).toBe('c2');
    expect(store.scrollRequest()?.chapterId).toBe('c2');

    // The toolbar button hides the tree and the reader takes the width.
    const toggle = page.querySelector<HTMLButtonElement>('[aria-label="Hide structure"]')!;
    toggle.click();
    await page.rendered();
    expect(page.querySelector('r2m-structure-tree')).toBeNull();
    expect(page.querySelector('.book__body--tree-hidden')).not.toBeNull();
    expect(page.querySelector('[aria-label="Show structure"]')).not.toBeNull();
  });

  it('a pause paragraph row carries a node-menu trigger for its delete', async () => {
    api.on('GET', `${BASE}/nodes/chapter/c1/children`, {
      paragraphs: [
        DIALOG('p1'),
        {
          id: 'pp',
          isPauseParagraph: true,
          items: [
            {
              id: 'pp-i',
              itemType: 'ChapterPause',
              text: '',
              characterId: null,
              audioFileName: null,
              voiceInstructions: null,
              orderKey: 'a',
              isPause: true,
            },
          ],
        },
      ],
    });
    const { page } = await start('projects/dune/book?mode=speakers');
    const pause = page.querySelector('.book__pause')!;
    const trigger = pause.querySelector<HTMLButtonElement>('.r2m-node-menu__trigger')!;
    expect(trigger.getAttribute('aria-label')).toBe('Actions for Chapter pause');
    trigger.click();
    await page.rendered();
    const entries = Array.from(page.querySelectorAll<HTMLElement>('[role="menu"] [data-entry]'));
    expect(entries.map((e) => e.dataset['entry'])).toEqual(['delete']);
  });

  it('a tree pick names the chapter in the URL', async () => {
    const { page } = await start('projects/dune/book');
    page.querySelector<HTMLElement>('[data-node-id=c2] .tree__title')!.click();
    for (let i = 0; i < 4; i++) await settle();
    const last = navigation.calls.at(-1)!;
    expect(last.history).toBe('replace');
    expect(new URL(last.url).searchParams.get('chapter')).toBe('c2');
  });

  it('expanding a tree node loads its children through the store', async () => {
    api
      .on('GET', `${BASE}/book`, { ...OVERVIEW, volumes: [{ id: 'v1', title: 'One' }, { id: 'v2', title: 'Two' }] })
      .on('GET', `${BASE}/nodes/volume/v2/children`, { parts: [{ id: 'p2', title: 'Late' }] })
      .on('GET', `${BASE}/nodes/part/p2/children`, { chapters: [{ id: 'c3', title: 'Three' }] });
    const { page, store } = await start('projects/dune/book');
    const tree = page.querySelector('r2m-structure-tree')!;
    const ids = () =>
      Array.from(tree.querySelectorAll<HTMLElement>('[role=treeitem]')).map((n) => n.dataset['nodeId']);
    expect(ids()).toEqual(['v1', 'v2']);

    tree.querySelector<HTMLElement>('[data-node-id=v2] .tree__toggle')!.click();
    for (let i = 0; i < 4; i++) await settle();
    expect([...store.expanded()]).toEqual(['v2']);
    expect(api.calls('GET', `${BASE}/nodes/volume/v2/children`)).toHaveLength(1);
    await page.rendered();
    // A volume with one part shows that part's chapters directly (buildTree).
    expect(ids()).toEqual(['v1', 'v2', 'c3']);
  });

  it('?chapter= opens at that chapter instead of the first', async () => {
    const { page, store } = await start('projects/dune/book?chapter=c2');
    expect(store.window()).toContain('c2');
    expect(store.scrollRequest()?.chapterId).toBe('c2');
    expect(page.getAttribute('data-current-chapter')).toBe('c2');
    // c2 was read first; c1 only afterwards, when the edge check filled in the chapter before.
    const order = api.requests.map((r) => r.path);
    expect(order.indexOf(`${BASE}/nodes/chapter/c2/children`)).toBeLessThan(
      order.indexOf(`${BASE}/nodes/chapter/c1/children`),
    );
  });

  it('?mode= sets the reader mode; switching mode mirrors it to the URL', async () => {
    const { page, store } = await start('projects/dune/book?mode=audio');
    expect(store.mode()).toBe('audio');
    // Audio mode reads voices with the chapter and shows the voice line per item.
    expect(api.calls('GET', `${BASE}/nodes/chapter/c1/voices`)).toHaveLength(1);
    const inC1 = (selector: string) =>
      page.querySelectorAll(`.r2m-paragraph[data-chapter-id=c1] ${selector}`).length;
    expect(inC1('[data-testid=voice-line]')).toBe(2);

    const group = page.querySelector('[role=radiogroup][aria-label="Reader mode"]')!;
    const radios = group.querySelectorAll<HTMLInputElement>('input[type=radio]');
    expect(Array.from(radios).map((r) => [r.value, r.checked])).toEqual([
      ['read', false],
      ['speakers', false],
      ['audio', true],
    ]);
    radios[1]!.checked = true;
    radios[1]!.dispatchEvent(new Event('change'));
    await settle();

    expect(store.mode()).toBe('speakers');
    const last = navigation.calls.at(-1)!;
    expect(last.history).toBe('replace');
    expect(new URL(last.url).searchParams.get('mode')).toBe('speakers');
    await page.rendered();
    expect(inC1('[data-testid=voice-line]')).toBe(0);
    expect(inC1('.r2m-item .r2m-speaker-chip')).toBe(2);

    // Back to Read drops the query.
    radios[0]!.checked = true;
    radios[0]!.dispatchEvent(new Event('change'));
    await settle();
    expect(new URL(navigation.calls.at(-1)!.url).searchParams.has('mode')).toBe(false);
  });

  it('a failed read shows the stale banner, and Refresh reads again', async () => {
    api.on('GET', `${BASE}/nodes/chapter/c1/children`, () => problem(500, 'db locked'));
    const { page, store } = await start('projects/dune/book');
    expect(store.stale()).toContain('db locked');
    const banner = page.querySelector('.book__stale')!;
    expect(text(banner)).toContain('This view may be out of date');

    api.on('GET', `${BASE}/nodes/chapter/c1/children`, { paragraphs: [DIALOG('p1')] });
    banner.querySelector('button')!.click();
    for (let i = 0; i < 4; i++) await settle();
    expect(store.stale()).toBeNull();
    expect(page.querySelector('.book__stale')).toBeNull();
    expect(page.querySelectorAll('.r2m-paragraph[data-chapter-id=c1]').length).toBe(1);
  });
});
