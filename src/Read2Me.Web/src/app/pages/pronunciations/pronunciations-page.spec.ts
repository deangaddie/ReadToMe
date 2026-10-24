import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import {
  type ProjectDetailDto,
  type ProjectStatusDto,
  type PronunciationDto,
  StepView,
  VIEWING_READ_ONLY,
} from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices, use } from '@app/core/services';
import type { Receipt } from '@app/live/live-messages';
import { REFETCH_DEBOUNCE_MS } from '@app/shared/debounced';
import { type ConfirmOptions, ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { installNavigation, settle } from '../../../testing/fake-navigation';
import type { PronunciationEditor } from './pronunciation-editor';
import { AUDIO_RUNNING } from './pronunciation-logic';
import type { PronunciationsPage } from './pronunciations-page';
import '../project/project-shell';
import './pronunciations-page';

const PROJECT = '/api/projects/magician';
const LIST = `${PROJECT}/pronunciations`;

const DETAIL: ProjectDetailDto = {
  folderName: 'magician',
  title: 'Magician',
  bookTitle: 'Magician',
  author: 'Raymond E. Feist',
  filename: 'magician.epub',
  fileType: 'Epub',
  coverImage: null,
  narratorOnlyMode: false,
  narrator: {
    characterId: 'narrator',
    displayName: 'Narrator',
    isLinked: false,
  },
};

const STATUS: ProjectStatusDto = {
  hasContent: true,
  characters: 3,
  charactersWithLines: 2,
  readyVoices: 1,
  items: { total: 8, withAudio: 2, unattributed: 0 },
  attribution: { remaining: 0, processing: false, queued: 0 },
  audio: { remaining: 6 },
  review: 0,
  volumeIds: ['v1'],
  nodes: {},
  pauseParagraphs: 0,
  checkpoints: {},
  revision: 1,
};

function entry(
  written: string,
  spoken: string,
  overrides: Partial<PronunciationDto> = {},
): PronunciationDto {
  return {
    id: written.toLowerCase(),
    writtenForm: written,
    spokenForm: spoken,
    matchCount: 4,
    itemCount: 3,
    withAudioCount: 0,
    ...overrides,
  };
}

const ENTRIES = [
  entry('Crydee', 'CRY-dee', { itemCount: 20, withAudioCount: 5 }),
  entry('Kulgan', 'KULL-gun', { itemCount: 9, withAudioCount: 3 }),
  entry('Pug', 'pug'),
];

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: 'pronunciations', tag: 'r2m-pronunciations-page' }],
  },
];

function receipt(facets: string): Receipt {
  return {
    folder: 'magician',
    mutationName: 'SetPronunciationMutation',
    mutationId: 'm',
    revision: 2,
    effects: {
      scope: 'Exact',
      facets,
      nodeIds: [],
      paragraphIds: [],
      paragraphItemIds: [],
      structural: [],
      changedNothing: false,
    },
    originId: 'elsewhere',
    isOwn: false,
  } as Receipt;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

let api: FakeApi;
let liveFake: FakeLive;
let confirms: ConfirmOptions[];
let list: PronunciationDto[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  liveFake = new FakeLive().install();
  override(ToastService, {
    success: () => undefined,
    info: () => undefined,
    problem: () => undefined,
  } as unknown as ToastService);
  confirms = [];
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return true;
    },
  } as unknown as ConfirmService);
  list = ENTRIES;
});
afterEach(() => document.body.replaceChildren());

async function render(path = 'projects/magician/pronunciations'): Promise<PronunciationsPage> {
  api.on('GET', PROJECT, DETAIL).on('GET', `${PROJECT}/status`, STATUS);
  api.on('GET', LIST, () => list);
  installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<PronunciationsPage>('r2m-pronunciations-page');
  if (!page) throw new Error('the pronunciations page did not render');
  await page.rendered();
  await page.rendered();
  return page;
}

const rows = (page: Element) =>
  Array.from(page.querySelectorAll('.pron__row')).map((r) =>
    Array.from(r.querySelectorAll('td'))
      .slice(0, 4)
      .map((td) => td.textContent?.trim()),
  );

async function rerender(page: PronunciationsPage): Promise<void> {
  for (let i = 0; i < 3; i++) await settle();
  await page.rendered();
}

function editorOf(page: Element): PronunciationEditor | null {
  return page.querySelector<PronunciationEditor>('r2m-pronunciation-editor');
}

describe('r2m-pronunciations-page', () => {
  it('lists each entry with its written and spoken forms, items and items with audio', async () => {
    const page = await render();
    expect(rows(page)).toEqual([
      ['Crydee', 'CRY-dee', '20', '5'],
      ['Kulgan', 'KULL-gun', '9', '3'],
      ['Pug', 'pug', '3', '–'],
    ]);
    expect(page.querySelector('.pron__hint')?.textContent).toContain('Pick a pronunciation');
  });

  it('says so when the book has none yet, offering Add pronunciation', async () => {
    list = [];
    const page = await render();
    expect(page.querySelector('.r2m-empty-state')?.textContent).toContain('No pronunciations yet');
    expect(page.querySelector('.r2m-empty-state__action')?.textContent).toContain(
      'Add pronunciation',
    );
  });

  it('search filters by written or spoken form', async () => {
    const page = await render();
    const search = page.querySelector<HTMLInputElement>('input[type=search]')!;
    search.value = 'gun';
    search.dispatchEvent(new Event('input'));
    await page.rendered();
    expect(rows(page).map((r) => r[0])).toEqual(['Kulgan']);
    search.value = 'zzz';
    search.dispatchEvent(new Event('input'));
    await page.rendered();
    expect(page.querySelector('.pron__none')?.textContent?.trim()).toBe(
      'No pronunciation matches "zzz".',
    );
  });

  it('?entry= opens that entry in the editor (the Book view’s Edit pencil), then drops the param', async () => {
    const page = await render('projects/magician/pronunciations?entry=kulgan');
    await rerender(page);
    expect(editorOf(page)?.entry?.writtenForm).toBe('Kulgan');
    expect(use(Router).query().get('entry')).toBeNull();
  });

  it('a row opens its entry in the editor and marks it; the editor closing empties the pane', async () => {
    const page = await render();
    page.querySelector<HTMLElement>('.pron__row[data-id=kulgan]')!.click();
    await rerender(page);
    expect(page.querySelector('.pron--editing')).not.toBeNull();
    expect(page.querySelector('.pron__row--selected')?.getAttribute('data-id')).toBe('kulgan');
    expect(editorOf(page)?.entry?.writtenForm).toBe('Kulgan');

    editorOf(page)!.dispatchEvent(new CustomEvent('editor-close'));
    await page.rendered();
    expect(editorOf(page)).toBeNull();
    expect(page.querySelector('.pron--editing')).toBeNull();
  });

  it('Add pronunciation opens an empty editor', async () => {
    const page = await render();
    page.querySelector<HTMLButtonElement>('[data-action=add]')!.click();
    await rerender(page);
    expect(editorOf(page)).not.toBeNull();
    expect(editorOf(page)!.entry).toBeNull();
  });

  it('rereads the list on a Pronunciations receipt, and not on others', async () => {
    const page = await render();
    const reads = () => api.calls('GET', LIST).length;
    const before = reads();

    liveFake.emit('receipt', receipt('Structure'));
    await sleep(REFETCH_DEBOUNCE_MS + 50);
    expect(reads()).toBe(before);

    list = [...ENTRIES, entry('Tomas', 'TOM-us')];
    liveFake.emit('receipt', receipt('Pronunciations'));
    await sleep(REFETCH_DEBOUNCE_MS + 50);
    await rerender(page);
    expect(reads()).toBe(before + 1);
    expect(rows(page).map((r) => r[0])).toEqual(['Crydee', 'Kulgan', 'Pug', 'Tomas']);
  });

  it('closes the editor when its entry is deleted elsewhere', async () => {
    const page = await render();
    page.querySelector<HTMLElement>('.pron__row[data-id=pug]')!.click();
    await rerender(page);
    list = ENTRIES.filter((e) => e.id !== 'pug');
    liveFake.emit('receipt', receipt('Pronunciations'));
    await sleep(REFETCH_DEBOUNCE_MS + 50);
    await rerender(page);
    expect(editorOf(page)).toBeNull();
  });

  it('a row’s delete confirms with the audio it costs, then deletes', async () => {
    api.on('POST', `${LIST}/impact`, {
      matchCount: 14,
      itemCount: 9,
      withAudioCount: 3,
      audioDiscardCount: 3,
      overlaps: [],
      duplicateOf: null,
      matches: [],
    });
    api.on('DELETE', `${LIST}/kulgan`, {
      audioDiscardCount: 3,
      needsAudioCount: 3,
    });
    const page = await render();
    page
      .querySelector<HTMLButtonElement>('.pron__row[data-id=kulgan] [data-action=delete-row]')!
      .click();
    await rerender(page);
    expect(confirms.map((c) => c.message)).toEqual([
      'Kulgan goes back to how the provider reads it. 3 items lose their audio.',
    ]);
    expect(api.calls('DELETE', `${LIST}/kulgan`)).toHaveLength(1);
    expect(editorOf(page)).toBeNull();
  });

  it('turns delete off while audio generation runs for the project, and tells the editor', async () => {
    const page = await render();
    liveFake.emit('itemStatus', {
      folder: 'magician',
      paragraphs: {},
      items: { i1: { status: 'Queued' } },
    });
    page.querySelector<HTMLElement>('.pron__row[data-id=kulgan]')!.click();
    await rerender(page);
    const del = page.querySelector<HTMLButtonElement>(
      '.pron__row[data-id=kulgan] [data-action=delete-row]',
    )!;
    expect(del.disabled).toBe(true);
    expect(del.closest('.r2m-blockable')?.getAttribute('data-tooltip')).toBe(AUDIO_RUNNING);
    expect(editorOf(page)!.audioInFlight).toBe(1);
  });

  it('shows the viewed step’s list read-only', async () => {
    use(StepView).view('magician', 's8');
    const page = await render();
    expect(api.calls('GET', LIST).at(-1)!.search).toBe('?at=s8');
    const add = page.querySelector<HTMLButtonElement>('[data-action=add]')!;
    expect(add.disabled).toBe(true);
    expect(add.closest('.r2m-blockable')?.getAttribute('data-tooltip')).toBe(VIEWING_READ_ONLY);
    const del = page.querySelector<HTMLButtonElement>('[data-action=delete-row]')!;
    expect(del.disabled).toBe(true);
    expect(del.closest('.r2m-blockable')?.getAttribute('data-tooltip')).toBe(VIEWING_READ_ONLY);

    page.querySelector<HTMLElement>('.pron__row[data-id=kulgan]')!.click();
    await rerender(page);
    expect(editorOf(page)!.readOnly).toBe(VIEWING_READ_ONLY);
  });

  it('guards leaving with an unsaved draft', async () => {
    const page = await render();
    page.querySelector<HTMLElement>('.pron__row[data-id=kulgan]')!.click();
    await rerender(page);
    expect(page.hasUnsavedChanges()).toBe(false);
    editorOf(page)!.spoken.set('KULL-gn');
    expect(page.hasUnsavedChanges()).toBe(true);
  });
});
