import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import type { BookOverviewDto, ParagraphDto } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import type { QueueMessage } from '@app/live/live-messages';
import { Preflight } from '@app/shared/preflight';
import { type ConfirmOptions, ConfirmService, PromptService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { STRUCTURE_RULES } from '../../../testing/structure-rules';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import { AudioGenerator } from './audio-generator';
import { BookEditor } from './book-editor';
import { BookStore } from './book-store';
import type { BookPage } from './book-page';
import { AudioSelectionStore, SelectionStore } from './selection-store';
import { SpeakerAssigner } from './speaker-assigner';
import '../project/project-shell';
import './book-page';

const BASE = '/api/projects/dune';

const OVERVIEW: BookOverviewDto = {
  hasContent: true,
  volumes: [{ id: 'v1', title: null }],
  characters: [{ id: 'h', name: 'Hardin', aliases: [] }],
  totalParts: 1,
  totalChapters: 2,
  revision: 1,
  structureRules: STRUCTURE_RULES,
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
let live: FakeLive;
let toasts: string[];
let ensureReady: ReturnType<typeof mock>;
/** Every confirm the page asked for, answered with {@link confirmAnswer}. */
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;

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
  live = new FakeLive().install();
  toasts = [];
  ensureReady = mock(async () => true);
  override(Preflight, { ensureReady } as unknown as Preflight);
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    info: (m: string) => toasts.push(m),
    warn: (m: string) => toasts.push(m),
    error: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
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

/** Opens a Node menu from its trigger and lists the entries it offers. */
async function menuEntries(page: BookPage, trigger: HTMLButtonElement) {
  trigger.click();
  await page.rendered();
  const entries = Array.from(page.querySelectorAll<HTMLElement>('[role="menu"] [data-entry]'));
  return entries.map((e) => e.dataset['entry']);
}

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

  it('a chapter that begins a Volume or Part the tree shows has their headings above it', async () => {
    api
      .on('GET', `${BASE}/book`, {
        ...OVERVIEW,
        volumes: [
          { id: 'v1', title: 'Book One' },
          { id: 'v2', title: null },
        ],
      })
      .on('GET', `${BASE}/nodes/volume/v1/children`, {
        parts: [
          { id: 'p1', title: 'Arrival' },
          { id: 'p2', title: null },
        ],
      })
      .on('GET', `${BASE}/nodes/part/p1/children`, { chapters: [{ id: 'c1', title: 'One' }] })
      .on('GET', `${BASE}/nodes/part/p2/children`, { chapters: [{ id: 'c2', title: 'Two' }] });
    const { page, store } = await start('projects/dune/book');
    await store.openChapter('c2');
    await store.loadPrevious(false);
    await page.rendered();

    const keys = Array.from(page.querySelectorAll('.r2m-vlist__row'), (r) =>
      r.getAttribute('data-row-key'),
    ).filter((k) => !k?.startsWith('paragraph:'));
    expect(keys).toEqual(['volume:v1', 'part:p1', 'chapter:c1', 'part:p2', 'chapter:c2']);
    const heading = (id: string) => {
      const el = page.querySelector(`.book__heading[data-node-id=${id}] h2`);
      return [el?.className.match(/book__(volume|part)/)?.[1], text(el)];
    };
    expect(heading('v1')).toEqual(['volume', 'Book One']);
    expect(heading('p1')).toEqual(['part', 'Arrival']);
    expect(heading('p2')).toEqual(['part', 'Untitled part 2']);
    const placeholder = (id: string) =>
      page
        .querySelector(`.book__heading[data-node-id=${id}] h2`)
        ?.classList.contains('r2m-placeholder-title');
    expect([placeholder('v1'), placeholder('p1'), placeholder('p2')]).toEqual([false, false, true]);
    // Opening a chapter still makes the chapter current, not the heading above it.
    expect(store.currentChapterId()).toBe('c2');
  });

  it('a single volume with a single part shows no Volume or Part heading', async () => {
    const { page } = await start('projects/dune/book');
    expect(page.querySelector('.book__volume, .book__part')).toBeNull();
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
    expect(titles()).toEqual(['The Encyclopedists', 'Untitled chapter 2']);
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
    // An untitled chapter's heading is its placeholder title, set apart as the tree's is.
    const headings = Array.from(page.querySelectorAll('.book__chapter'));
    expect(headings.map((h) => [text(h), h.classList.contains('r2m-placeholder-title')])).toEqual([
      ['The Encyclopedists', false],
      ['Untitled chapter 2', true],
    ]);

    // The toolbar button hides the tree and the reader takes the width.
    const toggle = page.querySelector<HTMLButtonElement>('[aria-label="Hide structure"]')!;
    toggle.click();
    await page.rendered();
    expect(page.querySelector('r2m-structure-tree')).toBeNull();
    expect(page.querySelector('.book__body--tree-hidden')).not.toBeNull();
    expect(page.querySelector('[aria-label="Show structure"]')).not.toBeNull();
  });

  it('a pause paragraph row carries a node-menu trigger for its inserts and delete', async () => {
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
    expect(entries.map((e) => e.dataset['entry'])).toEqual([
      'insert-node:Before',
      'insert-node:After',
      'delete',
    ]);
  });

  describe('chapter heading Node menu', () => {
    const headingTrigger = (page: BookPage, chapterId: string) =>
      page.querySelector<HTMLButtonElement>(
        `.book__heading[data-chapter-id=${chapterId}] .r2m-node-menu__trigger`,
      )!;
    const treeTrigger = (page: BookPage, chapterId: string) =>
      page.querySelector<HTMLButtonElement>(`[data-node-id=${chapterId}] .r2m-node-menu__trigger`)!;

    it.each(['read', 'speakers', 'audio'] as const)(
      'in %s mode offers exactly what the tree offers for the same chapter',
      async (mode) => {
        const { page } = await start(`projects/dune/book?mode=${mode}`);
        await page.rendered();
        for (const id of ['c1', 'c2']) {
          const fromTree = await menuEntries(page, treeTrigger(page, id));
          expect(await menuEntries(page, headingTrigger(page, id))).toEqual(fromTree);
        }
      },
    );

    it("lists the chapter's entries, merges hidden at the ends, shortcuts following the mode", async () => {
      const { page } = await start('projects/dune/book?mode=speakers');
      await page.rendered();
      expect(headingTrigger(page, 'c1').getAttribute('aria-label')).toBe(
        'Actions for The Encyclopedists',
      );
      expect(await menuEntries(page, headingTrigger(page, 'c1'))).toEqual([
        'select-unprocessed',
        'attribute-node',
        'release-narration',
        'edit-title',
        'split',
        'merge-next',
        'insert-node:Before',
        'insert-node:After',
        'insert-node:AtStart',
        'insert-node:AtEnd',
        'delete',
      ]);
      const last = await menuEntries(page, headingTrigger(page, 'c2'));
      expect(last).toContain('merge-previous');
      expect(last).not.toContain('merge-next');
    });

    it('is disabled while the chapter is busy, as the tree trigger is', async () => {
      api.on('GET', `${BASE}/status`, {
        revision: 1,
        nodes: {
          c1: {
            attributionRemaining: 2,
            audioRemaining: 0,
            review: 0,
            attributionQueued: 2,
            attributionProcessing: false,
            isDone: false,
          },
        },
        audio: { remaining: 0 },
      });
      const { page } = await start('projects/dune/book');
      await page.rendered();
      expect(treeTrigger(page, 'c1').disabled).toBe(true);
      expect(headingTrigger(page, 'c1').disabled).toBe(true);
      expect(headingTrigger(page, 'c2').disabled).toBe(false);
    });

    it('is disabled while the editor is locked', async () => {
      const { page, store } = await start('projects/dune/book');
      await page.rendered();
      expect(headingTrigger(page, 'c1').disabled).toBe(false);
      // A failed read leaves the view stale, which locks the editor.
      api.on('GET', `${BASE}/nodes/part/p1/children`, () => problem(500, 'db locked'));
      await store.refresh();
      await page.rendered();
      expect(store.stale()).toContain('db locked');
      expect(headingTrigger(page, 'c1').disabled).toBe(true);
    });

    it('Edit title starts from the stored title and both panes show the new one after the save', async () => {
      const prompts: unknown[] = [];
      override(PromptService, {
        text: async (o: unknown) => (prompts.push(o), 'Psychohistorians'),
      } as unknown as PromptService);
      api.on('POST', `${BASE}/commands`, { newEntityId: null });
      const { page, store } = await start('projects/dune/book');
      await page.rendered();

      headingTrigger(page, 'c2').click();
      await page.rendered();
      page.querySelector<HTMLButtonElement>('[role="menu"] [data-entry=edit-title]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      // An untitled chapter's rename starts empty, never from its placeholder.
      expect(prompts).toEqual([expect.objectContaining({ initial: '' })]);
      expect(api.calls('POST', `${BASE}/commands`).map((r) => r.body)).toEqual([
        { type: 'UpdateChapterTitle', chapterId: 'c2', title: 'Psychohistorians' },
      ]);

      // The receipt's reload reads the stored title back.
      api.on('GET', `${BASE}/nodes/part/p1/children`, {
        chapters: [
          { id: 'c1', title: 'The Encyclopedists' },
          { id: 'c2', title: 'Psychohistorians' },
        ],
      });
      await store.refresh();
      await page.rendered();
      expect(text(page.querySelector('.book__heading[data-chapter-id=c2] .book__chapter'))).toBe(
        'Psychohistorians',
      );
      expect(text(page.querySelector('[data-node-id=c2] .tree__title'))).toBe('Psychohistorians');
    });
  });

  describe('Volume and Part heading Node menu', () => {
    /** Two volumes (so both show), the first with two parts (so both show), one chapter each. */
    const startNested = async (path: string) => {
      api
        .on('GET', `${BASE}/book`, {
          ...OVERVIEW,
          volumes: [
            { id: 'v1', title: 'Book One' },
            { id: 'v2', title: null },
          ],
        })
        .on('GET', `${BASE}/nodes/volume/v1/children`, {
          parts: [
            { id: 'pa', title: 'Arrival' },
            { id: 'pb', title: null },
          ],
        })
        .on('GET', `${BASE}/nodes/part/pa/children`, { chapters: [{ id: 'c1', title: 'One' }] })
        .on('GET', `${BASE}/nodes/part/pb/children`, { chapters: [{ id: 'c2', title: 'Two' }] })
        .on('GET', `${BASE}/nodes/volume/v2/children`, { parts: [{ id: 'pc', title: null }] })
        .on('GET', `${BASE}/nodes/part/pc/children`, { chapters: [{ id: 'c3', title: 'Three' }] })
        .on('GET', `${BASE}/nodes/chapter/c3/children`, { paragraphs: [DIALOG('p4')] })
        .on('GET', `${BASE}/nodes/chapter/c3/voices`, {});
      const started = await start(path);
      await started.store.openChapter('c2');
      await started.store.loadPrevious(false);
      await started.page.rendered();
      // Open the first volume in the tree, so its parts' triggers show there too.
      const toggle = started.page.querySelector<HTMLElement>(
        '[role=treeitem][data-node-id=v1] .tree__toggle',
      )!;
      if (toggle.closest('[aria-expanded=false]')) toggle.click();
      for (let i = 0; i < 4; i++) await settle();
      return started;
    };
    const headingTrigger = (page: BookPage, id: string) =>
      page.querySelector<HTMLButtonElement>(
        `.book__heading[data-node-id=${id}] .r2m-node-menu__trigger`,
      )!;
    const treeTrigger = (page: BookPage, id: string) =>
      page.querySelector<HTMLButtonElement>(
        `[role=treeitem][data-node-id=${id}] .r2m-node-menu__trigger`,
      )!;
    const choose = async (page: BookPage, id: string, entry: string) => {
      headingTrigger(page, id).click();
      await page.rendered();
      page.querySelector<HTMLButtonElement>(`[role="menu"] [data-entry=${entry}]`)!.click();
      for (let i = 0; i < 4; i++) await settle();
    };

    it.each(['read', 'speakers', 'audio'] as const)(
      'in %s mode offers exactly what the tree offers for the same Volume or Part',
      async (mode) => {
        const { page } = await startNested(`projects/dune/book?mode=${mode}`);
        for (const id of ['v1', 'pa', 'pb']) {
          const fromTree = await menuEntries(page, treeTrigger(page, id));
          expect(fromTree.length).toBeGreaterThan(0);
          expect(await menuEntries(page, headingTrigger(page, id))).toEqual(fromTree);
        }
      },
    );

    it('hides the merges at the ends, as the tree does', async () => {
      const { page } = await startNested('projects/dune/book?mode=speakers');
      expect(headingTrigger(page, 'pa').getAttribute('aria-label')).toBe('Actions for Arrival');
      const first = await menuEntries(page, headingTrigger(page, 'pa'));
      expect(first).toContain('merge-next');
      expect(first).not.toContain('merge-previous');
      expect(first).toContain('release-narration');
      const last = await menuEntries(page, headingTrigger(page, 'pb'));
      expect(last).toContain('merge-previous');
      expect(last).not.toContain('merge-next');
      const volume = await menuEntries(page, headingTrigger(page, 'v1'));
      expect(volume).toContain('merge-next');
      expect(volume).not.toContain('merge-previous');
      // No whole-volume release, as in the tree.
      expect(volume).not.toContain('release-narration');
    });

    it('is disabled while the node is busy or the editor is locked, as the tree trigger is', async () => {
      api.on('GET', `${BASE}/status`, {
        revision: 1,
        nodes: {
          pa: {
            attributionRemaining: 2,
            audioRemaining: 0,
            review: 0,
            attributionQueued: 0,
            attributionProcessing: true,
            isDone: false,
          },
        },
        audio: { remaining: 0 },
      });
      const { page, store } = await startNested('projects/dune/book');
      expect(treeTrigger(page, 'pa').disabled).toBe(true);
      expect(headingTrigger(page, 'pa').disabled).toBe(true);
      expect(headingTrigger(page, 'pb').disabled).toBe(false);
      expect(headingTrigger(page, 'v1').disabled).toBe(false);

      // A failed read leaves the view stale, which locks the editor.
      api.on('GET', `${BASE}/nodes/part/pb/children`, () => problem(500, 'db locked'));
      await store.refresh();
      await page.rendered();
      expect(store.stale()).toContain('db locked');
      expect(headingTrigger(page, 'v1').disabled).toBe(true);
      expect(headingTrigger(page, 'pb').disabled).toBe(true);
    });

    it.each([
      ['v1', 'Book One', { type: 'UpdateVolumeTitle', volumeId: 'v1', title: 'Renamed' }],
      ['pb', '', { type: 'UpdatePartTitle', partId: 'pb', title: 'Renamed' }],
    ])(
      'Edit title on %s starts from the stored title, never the placeholder',
      async (id, initial, command) => {
        const prompts: unknown[] = [];
        override(PromptService, {
          text: async (o: unknown) => (prompts.push(o), 'Renamed'),
        } as unknown as PromptService);
        api.on('POST', `${BASE}/commands`, { newEntityId: null });
        const { page } = await startNested('projects/dune/book');

        await choose(page, id, 'edit-title');
        expect(prompts).toEqual([expect.objectContaining({ initial })]);
        expect(api.calls('POST', `${BASE}/commands`).map((r) => r.body)).toEqual([command]);
      },
    );

    it('runs the shortcuts and Release narration as the tree does', async () => {
      api
        .on('GET', `${BASE}/nodes/part/pa/paragraph-ids`, (_body, url) =>
          url.searchParams.get('unprocessedOnly') === 'true'
            ? [{ id: 'p2', chapterId: 'c1', partId: 'pa', volumeId: 'v1' }]
            : [],
        )
        .on('POST', `${BASE}/attribution/enqueue`, { enqueued: 1 });
      const { page } = await startNested('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      const release = mock(async () => true);
      use(SpeakerAssigner, page).releaseNarration = release;

      await choose(page, 'pa', 'select-unprocessed');
      expect(selection.ids()).toEqual(['p2']);

      await choose(page, 'v1', 'attribute-node');
      expect(api.calls('POST', `${BASE}/attribution/enqueue`).map((r) => r.body)).toEqual([
        { level: 'volume', nodeId: 'v1', unprocessedOnly: true },
      ]);

      // The release names the part as it shows, placeholder title included.
      await choose(page, 'pb', 'release-narration');
      expect(release).toHaveBeenCalledWith('Part', 'pb', 'Untitled part 2');
      expect(selection.count()).toBe(0);
    });
  });

  it('a tree pick names the chapter in the URL', async () => {
    const { page } = await start('projects/dune/book');
    page.querySelector<HTMLElement>('[data-node-id=c2] .tree__title')!.click();
    for (let i = 0; i < 4; i++) await settle();
    const last = navigation.calls.at(-1)!;
    expect(last.history).toBe('replace');
    expect(new URL(last.url).searchParams.get('chapter')).toBe('c2');
  });

  it('Enter on a focused tree chapter opens it, names it in the URL, marks it current and keeps the focus', async () => {
    const { page, store } = await start('projects/dune/book');
    const item = (id: string) =>
      page.querySelector<HTMLElement>(`r2m-structure-tree [data-node-id=${id}]`)!;
    item('c2').focus();
    item('c2').dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }),
    );
    for (let i = 0; i < 4; i++) await settle();
    await page.rendered();

    expect(store.currentChapterId()).toBe('c2');
    expect(page.getAttribute('data-current-chapter')).toBe('c2');
    expect(new URL(navigation.calls.at(-1)!.url).searchParams.get('chapter')).toBe('c2');
    expect(item('c2').getAttribute('aria-current')).toBe('true');
    expect(item('c1').hasAttribute('aria-current')).toBe(false);
    // The query-only navigation must not re-render the page and drop the focus to the body.
    expect(document.activeElement?.closest('[role=treeitem]')).toBe(item('c2'));
  });

  it('expanding a tree node loads its children through the store', async () => {
    api
      .on('GET', `${BASE}/book`, {
        ...OVERVIEW,
        volumes: [
          { id: 'v1', title: 'One' },
          { id: 'v2', title: 'Two' },
        ],
      })
      .on('GET', `${BASE}/nodes/volume/v2/children`, { parts: [{ id: 'p2', title: 'Late' }] })
      .on('GET', `${BASE}/nodes/part/p2/children`, { chapters: [{ id: 'c3', title: 'Three' }] });
    const { page, store } = await start('projects/dune/book');
    const tree = page.querySelector('r2m-structure-tree')!;
    const ids = () =>
      Array.from(tree.querySelectorAll<HTMLElement>('[role=treeitem]')).map(
        (n) => n.dataset['nodeId'],
      );
    expect(ids()).toEqual(['v1', 'v2']);

    tree.querySelector<HTMLElement>('[data-node-id=v2] .tree__toggle')!.click();
    for (let i = 0; i < 4; i++) await settle();
    expect([...store.expanded()]).toEqual(['v2']);
    expect(api.calls('GET', `${BASE}/nodes/volume/v2/children`)).toHaveLength(1);
    await page.rendered();
    // A volume with one part shows that part's chapters directly (buildTree).
    expect(ids()).toEqual(['v1', 'v2', 'c3']);
  });

  it('an opened chapter stays current while the list moves on its own, until the reader scrolls', async () => {
    const { page } = await start('projects/dune/book?chapter=c2');
    const list = page.querySelector('r2m-measured-list')!;
    const top = (type: string, index: number) =>
      list.dispatchEvent(new CustomEvent(type, { detail: index, bubbles: true }));
    await page.rendered();
    expect(page.getAttribute('data-current-chapter')).toBe('c2');

    // c1 prepended above c2, and the short c2 cannot reach the top: c1's header is on top.
    top('r2m-top-row', 0);
    await page.rendered();
    expect(page.getAttribute('data-current-chapter')).toBe('c2');

    top('r2m-reader-scroll', 0);
    await page.rendered();
    expect(page.getAttribute('data-current-chapter')).toBe('c1');
    // From then on the top row leads again.
    top('r2m-top-row', 3);
    await page.rendered();
    expect(page.getAttribute('data-current-chapter')).toBe('c2');
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

  describe('selection (ticket 12)', () => {
    const A = { chapterId: 'c1', partId: 'p1', volumeId: 'v1' };
    const bar = (page: BookPage) => page.querySelector('[data-testid=selection-bar]');
    /** The paragraph checkboxes of chapter 1 (the window may hold chapter 2 as well). */
    const rowBoxes = (page: BookPage) =>
      Array.from(
        page.querySelectorAll<HTMLInputElement>(
          '.r2m-paragraph[data-chapter-id=c1] > .r2m-paragraph__gutter input[type=checkbox]',
        ),
      );

    it('a tree checkbox reads the node paragraph ids, selects them and shows the action bar', async () => {
      api.on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, [
        { id: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' },
        { id: 'p2', chapterId: 'c1', partId: 'p1', volumeId: 'v1' },
      ]);
      const { page } = await start('projects/dune/book?mode=speakers');
      expect(bar(page)).toBeNull();

      const box = page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!;
      box.click();
      for (let i = 0; i < 4; i++) await settle();
      await page.rendered();

      const selection = use(SelectionStore, page);
      expect(selection.ids()).toEqual(['p1', 'p2']);
      expect(selection.nodeState('chapter', 'c1')).toBe('checked');
      expect(text(page.querySelector('[data-testid=selection-count]'))).toBe('2 paragraphs');
      expect(page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!.checked).toBe(
        true,
      );
      expect(rowBoxes(page).map((b) => b.checked)).toEqual([true, true]);

      // Unticking reads again and removes exactly those.
      api.on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, [
        { id: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' },
      ]);
      page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(selection.ids()).toEqual(['p2']);
      expect(api.calls('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`)).toHaveLength(2);
    });

    it('Attribute gates on preflight, posts the selected ids, reports the count and clears the selection', async () => {
      api.on('POST', `${BASE}/attribution/enqueue-paragraphs`, { enqueued: 1 });
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      selection.toggle('p1', A, true);
      await page.rendered();
      expect(text(page.querySelector('[data-testid=selection-count]'))).toBe('1 paragraph');

      page.querySelector<HTMLButtonElement>('[data-action=attribute-selection]')!.click();
      for (let i = 0; i < 4; i++) await settle();

      expect(ensureReady).toHaveBeenCalledWith('attribution');
      expect(
        api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`).map((r) => r.body),
      ).toEqual([{ paragraphIds: ['p1'] }]);
      expect(toasts).toEqual(['Queued 1 paragraph']);
      expect(selection.count()).toBe(0);
      // Nothing selected had an assigned speaker: no permanent-change confirm.
      expect(confirms).toEqual([]);
    });

    it('Attribute confirms when a selected paragraph has an assigned speaker, reading chapters not in the window', async () => {
      api.on('POST', `${BASE}/attribution/enqueue-paragraphs`, { enqueued: 2 });
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, {
        paragraphs: [DIALOG('p1'), DIALOG('p2')],
      });
      const far = DIALOG('p9');
      far.items[0]!.characterId = 'h';
      api.on('GET', `${BASE}/nodes/chapter/c9/children`, { paragraphs: [far] });
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      selection.toggle('p1', A, true);
      selection.toggle('p9', { chapterId: 'c9', partId: 'p1', volumeId: 'v1' }, true);
      await page.rendered();

      confirmAnswer = false;
      page.querySelector<HTMLButtonElement>('[data-action=attribute-selection]')!.click();
      for (let i = 0; i < 6; i++) await settle();
      expect(confirms).toEqual([
        {
          title: 'Attribute?',
          message:
            'Re-attributes 2 paragraphs: 1 assigned speaker is replaced. ' +
            'Getting a replaced speaker back means assigning it again by hand.',
          confirmLabel: 'Attribute',
          destructive: true,
        },
      ]);
      expect(api.calls('GET', `${BASE}/nodes/chapter/c9/children`)).toHaveLength(1);
      expect(api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`)).toHaveLength(0);
      expect(selection.count()).toBe(2);

      confirmAnswer = true;
      page.querySelector<HTMLButtonElement>('[data-action=attribute-selection]')!.click();
      for (let i = 0; i < 6; i++) await settle();
      expect(
        api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`).map((r) => r.body),
      ).toEqual([{ paragraphIds: ['p1', 'p9'] }]);
      expect(selection.count()).toBe(0);
    });

    it('a cancelled preflight sends nothing and keeps the selection', async () => {
      ensureReady.mockResolvedValueOnce(false);
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      selection.toggle('p1', A, true);
      await page.rendered();
      page.querySelector<HTMLButtonElement>('[data-action=attribute-selection]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(api.calls('POST', `${BASE}/attribution/enqueue-paragraphs`)).toHaveLength(0);
      expect(selection.count()).toBe(1);
      expect(toasts).toEqual([]);
    });

    it('Bulk assign hands the selection to the assigner and is disarmed while attribution runs', async () => {
      const { page } = await start('projects/dune/book');
      const selection = use(SelectionStore, page);
      const assigner = use(SpeakerAssigner, page);
      const assign = mock(async () => undefined);
      assigner.assign = assign;
      selection.toggle('p1', A, true);
      await page.rendered();

      const bulk = page.querySelector<HTMLButtonElement>('[data-action=bulk-assign]')!;
      expect(bulk.disabled).toBe(false);
      // happy-dom has no popover: the menu opens on the `toggle` event the browser would fire.
      const menu = page.querySelector('.book__bulk-menu')!;
      menu.dispatchEvent(Object.assign(new Event('toggle'), { newState: 'open' }));
      await page.rendered();
      await page.querySelector('r2m-speaker-menu')!.rendered();
      page.querySelector<HTMLButtonElement>('.r2m-speaker-menu__row')!.click();
      expect(assign).toHaveBeenCalledWith({ kind: 'selection', paragraphIds: ['p1'] }, 'h');

      live.queue.set({ attribution: { isBusy: true } } as unknown as QueueMessage);
      await page.rendered();
      expect(bulk.disabled).toBe(true);
      expect(bulk.getAttribute('data-tooltip')).toContain('waits until attribution has finished');
    });

    it('Audio mode hides the paragraph checkboxes and bar; the paragraph selection is kept aside', async () => {
      const { page } = await start('projects/dune/book?mode=audio');
      const selection = use(SelectionStore, page);
      selection.toggle('p1', { chapterId: 'c1', partId: null, volumeId: null }, true);
      await page.rendered();

      expect(rowBoxes(page)).toEqual([]);
      expect(bar(page)).toBeNull();
      // The tree's checkboxes now select items, and read as unchecked over that selection.
      const treeBox = page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!;
      expect(treeBox.getAttribute('aria-label')).toBe('Select items of The Encyclopedists');
      expect(treeBox.checked).toBe(false);
      expect(selection.count()).toBe(1);
      // The item checkboxes are there, off for the lines nobody can read yet.
      const items = page.querySelectorAll<HTMLInputElement>(
        '.r2m-paragraph[data-chapter-id=c1] .r2m-item input[type=checkbox]',
      );
      expect(Array.from(items, (b) => b.disabled)).toEqual([true, true]);
    });

    it('Select unprocessed and Attribute unprocessed from a node menu act on the node', async () => {
      api
        .on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, (_body, url) =>
          url.searchParams.get('unprocessedOnly') === 'true'
            ? [{ id: 'p2', chapterId: 'c1', partId: 'p1', volumeId: 'v1' }]
            : [],
        )
        .on('POST', `${BASE}/attribution/enqueue`, { enqueued: 2 });
      const { page } = await start('projects/dune/book?mode=speakers');
      const open = async () => {
        page.querySelector<HTMLButtonElement>('[data-node-id=c1] .r2m-node-menu__trigger')!.click();
        await page.rendered();
      };

      await open();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=select-unprocessed]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();
      expect(use(SelectionStore, page).ids()).toEqual(['p2']);

      await open();
      page.querySelector<HTMLButtonElement>('[role="menu"] [data-entry=attribute-node]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(ensureReady).toHaveBeenCalledTimes(1);
      expect(api.calls('POST', `${BASE}/attribution/enqueue`).map((r) => r.body)).toEqual([
        { level: 'chapter', nodeId: 'c1', unprocessedOnly: true },
      ]);
      expect(toasts).toEqual(['Queued 2 paragraphs']);
    });

    it('Release narration from a node menu releases the node, then clears the selection and its stale totals', async () => {
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      const assigner = use(SpeakerAssigner, page);
      const release = mock(async () => true);
      assigner.releaseNarration = release;
      selection.toggle('p1', A, true);
      selection.learnTotals({ 'part-x': 3 });

      page.querySelector<HTMLButtonElement>('[data-node-id=c1] .r2m-node-menu__trigger')!.click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=release-narration]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();

      expect(release).toHaveBeenCalledWith('Chapter', 'c1', 'The Encyclopedists');
      expect(selection.count()).toBe(0);
      expect(selection.totals()['part-x']).toBeUndefined();
      // The loaded chapter's total is relearnt straight away.
      expect(selection.totals()['c1']).toBeDefined();
      // The release itself never queues attribution.
      expect(api.calls('POST', `${BASE}/attribution/enqueue`)).toHaveLength(0);
    });

    it('a chapter heading menu runs the shortcuts and Release narration as the tree does', async () => {
      api
        .on('GET', `${BASE}/nodes/chapter/c1/paragraph-ids`, (_body, url) =>
          url.searchParams.get('unprocessedOnly') === 'true'
            ? [{ id: 'p2', chapterId: 'c1', partId: 'p1', volumeId: 'v1' }]
            : [],
        )
        .on('POST', `${BASE}/attribution/enqueue`, { enqueued: 1 });
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      const release = mock(async () => true);
      use(SpeakerAssigner, page).releaseNarration = release;
      const choose = async (chapterId: string, entry: string) => {
        page
          .querySelector<HTMLButtonElement>(
            `.book__heading[data-chapter-id=${chapterId}] .r2m-node-menu__trigger`,
          )!
          .click();
        await page.rendered();
        page.querySelector<HTMLButtonElement>(`[role="menu"] [data-entry=${entry}]`)!.click();
        for (let i = 0; i < 4; i++) await settle();
      };

      await choose('c1', 'select-unprocessed');
      expect(selection.ids()).toEqual(['p2']);

      await choose('c1', 'attribute-node');
      expect(api.calls('POST', `${BASE}/attribution/enqueue`).map((r) => r.body)).toEqual([
        { level: 'chapter', nodeId: 'c1', unprocessedOnly: true },
      ]);

      // The release names the chapter as it shows, placeholder title included.
      await choose('c2', 'release-narration');
      expect(release).toHaveBeenCalledWith('Chapter', 'c2', 'Untitled chapter 2');
      expect(selection.count()).toBe(0);
    });

    it('a release that writes nothing keeps the selection', async () => {
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      use(SpeakerAssigner, page).releaseNarration = mock(async () => false);
      selection.toggle('p1', A, true);

      page.querySelector<HTMLButtonElement>('[data-node-id=c1] .r2m-node-menu__trigger')!.click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=release-narration]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();

      expect(selection.count()).toBe(1);
    });

    it("Release narration from a narration-only paragraph's menu releases that paragraph and clears the selection", async () => {
      const narration: ParagraphDto = {
        id: 'q1',
        isPauseParagraph: false,
        items: [{ ...DIALOG('q1').items[0]!, itemType: 'Narration', text: 'Q: Your name?' }],
      };
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, {
        paragraphs: [DIALOG('p1'), narration],
      });
      const { page } = await start('projects/dune/book?mode=speakers');
      const selection = use(SelectionStore, page);
      const release = mock(async () => true);
      use(SpeakerAssigner, page).releaseNarration = release;
      selection.toggle('p1', A, true);

      page
        .querySelector<HTMLButtonElement>('[data-paragraph-id=q1] .r2m-paragraph__menu button')!
        .click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=release-narration]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();

      expect(release).toHaveBeenCalledWith('Paragraph', 'q1', 'Q: Your name?');
      expect(selection.count()).toBe(0);

      // A dialog-bearing row offers no release.
      page
        .querySelector<HTMLButtonElement>('[data-paragraph-id=p1] .r2m-paragraph__menu button')!
        .click();
      await page.rendered();
      expect(page.querySelector('[role="menu"] [data-entry=release-narration]')).toBeNull();
    });
  });

  describe('audio selection (ticket 13)', () => {
    const NARRATION = (id: string): ParagraphDto => ({
      id,
      isPauseParagraph: false,
      items: [
        {
          id: `${id}-i`,
          itemType: 'Narration',
          text: `${id} narration`,
          characterId: 'narrator',
          audioFileName: null,
          voiceInstructions: null,
          orderKey: 'a',
          isPause: false,
        },
      ],
    });
    const REF = { id: 'p1-i', paragraphId: 'p1', chapterId: 'c1', partId: 'p1', volumeId: 'v1' };

    it('a tree checkbox reads the node item ids, selects them and shows the item action bar', async () => {
      api
        .on('GET', `${BASE}/nodes/chapter/c1/children`, {
          paragraphs: [NARRATION('p1'), DIALOG('p2')],
        })
        .on('GET', `${BASE}/nodes/chapter/c1/item-ids`, [REF]);
      const { page } = await start('projects/dune/book?mode=audio');
      expect(page.querySelector('[data-testid=selection-bar]')).toBeNull();

      page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!.click();
      for (let i = 0; i < 4; i++) await settle();
      await page.rendered();

      const selection = use(AudioSelectionStore, page);
      expect(selection.ids()).toEqual(['p1-i']);
      expect(selection.nodeState('chapter', 'c1')).toBe('checked');
      expect(page.querySelector<HTMLInputElement>('[data-node-id=c1] .tree__select')!.checked).toBe(
        true,
      );
      expect(text(page.querySelector('[data-testid=selection-count]'))).toBe('1 item');
      const rows = Array.from(
        page.querySelectorAll<HTMLInputElement>(
          '.r2m-paragraph[data-chapter-id=c1] .r2m-item input[type=checkbox]',
        ),
      );
      expect(rows.map((b) => [b.checked, b.disabled])).toEqual([
        [true, false],
        [false, true],
      ]);
      expect(use(SelectionStore, page).count()).toBe(0);
    });

    it('Select needs audio reads with the filter and narrator-only flag; Generate audio queues and clears', async () => {
      api
        .on('GET', BASE, {
          folderName: 'dune',
          title: 'Dune',
          narrator: null,
          narratorOnlyMode: true,
        })
        .on('GET', `${BASE}/nodes/chapter/c1/children`, { paragraphs: [NARRATION('p1')] })
        .on('GET', `${BASE}/nodes/chapter/c1/item-ids`, (_body, url) =>
          url.searchParams.get('needsAudioOnly') === 'true' &&
          url.searchParams.get('narratorOnlyMode') === 'true'
            ? [REF]
            : [],
        );
      const { page } = await start('projects/dune/book?mode=audio');
      const generator = use(AudioGenerator, page);
      const enqueueItems = mock(async () => true);
      generator.enqueueItems = enqueueItems;

      page.querySelector<HTMLButtonElement>('[data-node-id=c1] .r2m-node-menu__trigger')!.click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=select-needs-audio]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();
      await page.rendered();

      const selection = use(AudioSelectionStore, page);
      expect(selection.ids()).toEqual(['p1-i']);
      page.querySelector<HTMLButtonElement>('[data-action=generate-audio-selection]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(enqueueItems).toHaveBeenCalledWith(['p1-i']);
      expect(selection.count()).toBe(0);
      expect(confirms).toEqual([]);
    });

    it('Generate audio confirms when a selected item already has a take', async () => {
      const taken = NARRATION('p1');
      taken.items[0]!.audioFileName = 'p1.wav';
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, { paragraphs: [taken] });
      const { page } = await start('projects/dune/book?mode=audio');
      const generator = use(AudioGenerator, page);
      const enqueueItems = mock(async () => true);
      generator.enqueueItems = enqueueItems;
      const selection = use(AudioSelectionStore, page);
      selection.add([REF]);
      await page.rendered();

      confirmAnswer = false;
      page.querySelector<HTMLButtonElement>('[data-action=generate-audio-selection]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(confirms).toEqual([
        {
          title: 'Generate audio?',
          message: 'Replaces existing audio on 1 item. The current takes cannot be brought back.',
          confirmLabel: 'Generate audio',
          destructive: true,
        },
      ]);
      expect(enqueueItems).not.toHaveBeenCalled();
      expect(selection.count()).toBe(1);

      confirmAnswer = true;
      page.querySelector<HTMLButtonElement>('[data-action=generate-audio-selection]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(enqueueItems).toHaveBeenCalledWith(['p1-i']);
      expect(selection.count()).toBe(0);
    });

    it('Generate audio for this node hands the node to the generator', async () => {
      api.on('GET', `${BASE}/nodes/chapter/c1/children`, { paragraphs: [NARRATION('p1')] });
      const { page } = await start('projects/dune/book?mode=audio');
      const generator = use(AudioGenerator, page);
      const enqueueNode = mock(async () => true);
      generator.enqueueNode = enqueueNode;

      page.querySelector<HTMLButtonElement>('[data-node-id=c1] .r2m-node-menu__trigger')!.click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=generate-audio-node]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();
      expect(enqueueNode).toHaveBeenCalledWith('chapter', 'c1', false);

      // The chapter's heading on the Book page does the same.
      enqueueNode.mockClear();
      page
        .querySelector<HTMLButtonElement>(
          '.book__heading[data-chapter-id=c1] .r2m-node-menu__trigger',
        )!
        .click();
      await page.rendered();
      page
        .querySelector<HTMLButtonElement>('[role="menu"] [data-entry=generate-audio-node]')!
        .click();
      for (let i = 0; i < 4; i++) await settle();
      expect(enqueueNode).toHaveBeenCalledWith('chapter', 'c1', false);
    });
  });

  describe('book actions (ticket 11)', () => {
    const open = async (page: BookPage) => {
      page.querySelector<HTMLButtonElement>('[data-testid=book-actions]')!.click();
      await page.rendered();
    };

    it('offers the title entries that add navigation, pauses, reread and manual reread', async () => {
      const { page } = await open1();
      const actions = Array.from(
        page.querySelectorAll<HTMLElement>('[aria-label="Book actions"][role=menu] [data-action]'),
        (e) => e.dataset['action'],
      );
      // One volume, one part, two chapters: only the chapter titles add navigation.
      expect(actions).toEqual([
        'add-book-title',
        'add-chapter-titles',
        'add-pauses',
        'edit-with-ai',
        'reread',
        'manual-reread',
      ]);
    });

    it('stay shut until the overview has loaded, so the entries cannot move under the pointer', async () => {
      const overview = Promise.withResolvers<BookOverviewDto>();
      api.on('GET', `${BASE}/book`, () => overview.promise);
      const { page } = await start('projects/dune/book');
      const button = page.querySelector<HTMLButtonElement>('[data-testid=book-actions]')!;
      expect(button.disabled).toBe(true);

      overview.resolve(OVERVIEW);
      for (let i = 0; i < 4; i++) await settle();
      await page.rendered();
      expect(button.disabled).toBe(false);
    });

    it('Add pauses confirms with the chapter count, then posts its command through the editor', async () => {
      api.on('POST', `${BASE}/commands`, { newEntityId: null });
      const { page } = await open1();
      confirmAnswer = false;
      page.querySelector<HTMLButtonElement>('[data-action=add-pauses]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(confirms).toEqual([
        {
          title: 'Add pauses?',
          message:
            'Inserts a pause after every chapter title (2 chapters). ' +
            'Removing them later means deleting each one.',
          confirmLabel: 'Add pauses',
          destructive: false,
        },
      ]);
      expect(api.calls('POST', `${BASE}/commands`)).toHaveLength(0);

      confirmAnswer = true;
      await open(page);
      page.querySelector<HTMLButtonElement>('[data-action=add-pauses]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(api.calls('POST', `${BASE}/commands`).map((r) => r.body)).toEqual([
        { type: 'AddPauses' },
      ]);
    });

    it('shows a busy "Saving…" chip on the toolbar while the write is in flight', async () => {
      const reply = Promise.withResolvers<unknown>();
      api.on('POST', `${BASE}/commands`, () => reply.promise);
      const { page } = await open1();
      expect(page.querySelector('[data-testid=book-saving]')).toBeNull();

      page.querySelector<HTMLButtonElement>('[data-action=add-pauses]')!.click();
      await settle();
      await page.rendered();
      const chip = page.querySelector('[data-testid=book-saving] .r2m-status-chip');
      expect(chip?.classList.contains('r2m-status-chip--busy')).toBe(true);
      expect(text(chip!)).toContain('Saving…');
      // Clearing once the write settles is the editor's `saving` (book-editor.spec).
      reply.resolve({ newEntityId: null });
    });

    it('Reread confirms destructively, then rereads', async () => {
      api.on('POST', `${BASE}/import`, undefined);
      confirmAnswer = false;
      const { page } = await open1();
      page.querySelector<HTMLButtonElement>('[data-action=reread]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(confirms).toEqual([
        expect.objectContaining({
          title: 'Reread the book?',
          destructive: true,
          confirmLabel: 'Delete and reread',
        }),
      ]);
      expect(api.calls('POST', `${BASE}/import`)).toHaveLength(0);

      confirmAnswer = true;
      await open(page);
      page.querySelector<HTMLButtonElement>('[data-action=reread]')!.click();
      for (let i = 0; i < 6; i++) await settle();
      expect(api.calls('POST', `${BASE}/import`)).toHaveLength(1);
    });

    it('Reread names the confirmed and skipped steps it clears when a checkpoint row exists (guidance-primitives 14)', async () => {
      api.on('GET', `${BASE}/status`, {
        revision: 1,
        nodes: {},
        audio: { remaining: 0 },
        checkpoints: { structure: { state: 'skipped', stale: false, at: '2026-10-18' } },
      });
      confirmAnswer = false;
      const { page } = await open1();
      page.querySelector<HTMLButtonElement>('[data-action=reread]')!.click();
      for (let i = 0; i < 4; i++) await settle();
      expect(confirms[0]?.message).toContain('Clears your confirmed and skipped steps.');
    });

    it('Manual reread opens the dialog and sends its request', async () => {
      api.on('POST', `${BASE}/import/manual`, undefined);
      const { page } = await open1();
      page.querySelector<HTMLButtonElement>('[data-action=manual-reread]')!.click();
      await settle();
      const dialog = document.querySelector('r2m-manual-reread-dialog')!;
      await dialog.rendered();
      const prefix = dialog.querySelector<HTMLInputElement>('input[data-level=chapter]')!;
      prefix.value = 'Chapter';
      prefix.dispatchEvent(new Event('input'));
      await dialog.rendered();
      dialog.querySelector<HTMLButtonElement>('.manual__submit')!.click();
      for (let i = 0; i < 6; i++) await settle();
      expect(api.calls('POST', `${BASE}/import/manual`).map((r) => r.body)).toEqual([
        {
          hasMultipleVolumes: false,
          hasMultipleParts: false,
          volume: null,
          part: null,
          chapter: { mode: 'Prefix', prefix: 'Chapter' },
        },
      ]);
    });

    it('Edit with AI opens the dialog over the editor and reports what it applied', async () => {
      const { page } = await open1();
      page.querySelector<HTMLButtonElement>('[data-action=edit-with-ai]')!.click();
      await settle();
      const dialog = document.querySelector('r2m-edit-with-ai-dialog')!;
      await dialog.rendered();
      expect(dialog.closest('dialog')?.className).toBe('r2m-dialog r2m-dialog--fullscreen');
      expect(dialog.data.editor).toBe(use(BookEditor, page));

      dialog.dispatchEvent(new CustomEvent('r2m-close', { detail: { applied: 2 } }));
      for (let i = 0; i < 4; i++) await settle();
      expect(document.querySelector('r2m-edit-with-ai-dialog')).toBeNull();
      expect(toasts).toEqual(['Applied 2 edits.']);
    });

    async function open1() {
      const started = await start('projects/dune/book');
      await open(started.page);
      return started;
    }
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
