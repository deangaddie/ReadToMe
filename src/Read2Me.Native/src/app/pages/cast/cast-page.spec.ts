import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import { ActivityStore } from '@app/activity/activity-store';
import { LlmStreamFeed } from '@app/activity/stream-feed';
import type { CharacterSummaryDto, ProjectDetailDto, ProjectStatusDto } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { signal } from '@app/core/signals';
import { Preflight } from '@app/shared/preflight';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { IDLE_VOICE_BATCH } from '@app/live/live-state';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import type { CastPage } from './cast-page';
import '../project/project-shell';
import './cast-page';

const BASE = '/api/projects/dune';
const SUMMARY = `${BASE}/characters/summary`;
const COMMANDS = `${BASE}/commands`;

function detail(linked = false): ProjectDetailDto {
  return {
    folderName: 'dune',
    title: 'Dune',
    bookTitle: 'Dune',
    author: 'Frank Herbert',
    filename: 'dune.epub',
    fileType: 'Epub',
    coverImage: null,
    narratorOnlyMode: false,
    narrator: linked
      ? { characterId: 'paul', displayName: 'Paul', isLinked: true }
      : { characterId: 'narrator', displayName: 'Narrator', isLinked: false },
  };
}

const STATUS: ProjectStatusDto = {
  hasContent: true,
  characters: 3,
  charactersWithLines: 2,
  readyVoices: 1,
  items: { total: 8, withAudio: 2, unattributed: 3 },
  attribution: { remaining: 3, processing: false, queued: 0 },
  audio: { remaining: 4 },
  review: 0,
  volumeIds: ['v1'],
  nodes: {},
  revision: 1,
};

function row(name: string, overrides: Partial<CharacterSummaryDto> = {}): CharacterSummaryDto {
  return {
    id: name.toLowerCase(),
    name,
    aliases: [],
    lineCount: 0,
    voiceCount: 0,
    readyVoiceCount: 0,
    isNarrator: name === 'Narrator',
    narratesBook: false,
    ...overrides,
  };
}

const ROWS = [
  row('Narrator'),
  row('Paul', {
    lineCount: 5,
    voiceCount: 2,
    readyVoiceCount: 1,
    aliases: [{ id: 'a', name: 'Usul' }],
  }),
  row('Jessica', { lineCount: 9 }),
];

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [
      { path: 'cast', tag: 'r2m-cast-page' },
      { path: 'cast/:characterId', tag: 'r2m-cast-page' },
    ],
  },
];

let api: FakeApi;
let navigation: FakeNavigation;
let toasts: string[];
let liveFake: FakeLive;
let preflightTasks: string[];
let preflightAnswer: boolean;
let drawerOpen: ReturnType<typeof signal<boolean>>;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  liveFake = new FakeLive().install();
  toasts = [];
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
  preflightTasks = [];
  preflightAnswer = true;
  override(Preflight, {
    ensureReady: async (task: string) => {
      preflightTasks.push(task);
      return preflightAnswer;
    },
  } as unknown as Preflight);
  drawerOpen = signal(false);
  override(ActivityStore, { drawerOpen } as unknown as ActivityStore);
  override(LlmStreamFeed, {
    acquire: () => undefined,
    release: () => undefined,
    events: signal([]),
    maxUnits: 50,
  } as unknown as LlmStreamFeed);
});
afterEach(() => document.body.replaceChildren());

/** The cast page under the project shell, through a root outlet as in the app. */
async function render(
  path = 'projects/dune/cast',
  rows = ROWS,
  linked = false,
  voiceNames: Record<string, string[]> = {},
) {
  api.on('GET', BASE, detail(linked)).on('GET', `${BASE}/status`, STATUS);
  api.on('GET', SUMMARY, rows);
  for (const r of rows) {
    api.on('GET', `${BASE}/characters/${r.id}/lines`, []);
    api.on('GET', `${BASE}/characters/${r.id}/voices`, {
      defaultVoiceId: null,
      voices: (voiceNames[r.id] ?? []).map((name) => ({ name })),
    });
    api.on('GET', `${BASE}/characters/${r.id}/voice-rules`, []);
    api.on('GET', `${BASE}/characters/${r.id}/voice-rules/preview`, []);
  }
  navigation = installNavigation(path);
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<CastPage>('r2m-cast-page');
  if (!page) throw new Error('the cast page did not render');
  await page.rendered();
  await page.rendered();
  return { page, router };
}

const names = (page: Element) =>
  Array.from(page.querySelectorAll('.cast__name')).map((n) => n.textContent?.trim());
const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('r2m-cast-page', () => {
  it('lists the roster with the Narrator first, alias counts and readiness chips', async () => {
    const { page } = await render();
    expect(page.querySelector('.r2m-page-header__subtitle')?.textContent).toBe('Dune');
    expect(names(page)).toEqual(['Narrator', 'Jessica', 'Paul']);
    const paul = page.querySelector('.cast__row[data-character-id="paul"]')!;
    expect(paul.getAttribute('href')).toBe('projects/dune/cast/paul');
    expect(paul.querySelector('.cast__aliases')?.textContent).toBe('1');
    expect(paul.querySelector('.cast__aliases')?.getAttribute('data-tooltip')).toBe('Usul');
    expect(text(paul.querySelector('.r2m-status-chip'))).toContain('1 / 2');
    expect(
      page.querySelector('.cast__row[data-character-id="narrator"] .cast__icon--book'),
    ).not.toBeNull();
    expect(text(page.querySelector('.cast__detail'))).toContain('Select a character');
    expect(page.querySelector('r2m-narrator-banner')?.textContent).toContain('Say who tells it');
  });

  it('search filters by name or alias and sort reorders', async () => {
    const { page } = await render();
    const search = page.querySelector<HTMLInputElement>('input[type=search]')!;
    search.value = 'usul';
    search.dispatchEvent(new Event('input'));
    await page.rendered();
    expect(names(page)).toEqual(['Paul']);
    search.value = 'zzz';
    search.dispatchEvent(new Event('input'));
    await page.rendered();
    expect(text(page.querySelector('.cast__none'))).toBe('No character matches "zzz".');
    search.value = '';
    search.dispatchEvent(new Event('input'));
    const sort = page.querySelector<HTMLSelectElement>('.cast__sort select')!;
    sort.value = 'lines';
    sort.dispatchEvent(new Event('change'));
    await page.rendered();
    expect(names(page)).toEqual(['Narrator', 'Jessica', 'Paul']);
    sort.value = 'readiness';
    sort.dispatchEvent(new Event('change'));
    await page.rendered();
    expect(names(page)).toEqual(['Narrator', 'Paul', 'Jessica']);
  });

  it('the route selects the character and marks its row', async () => {
    const { page } = await render('projects/dune/cast/paul');
    expect(page.querySelector('.cast--detail')).not.toBeNull();
    expect(page.querySelector('.cast__row--selected')?.getAttribute('data-character-id')).toBe(
      'paul',
    );
    expect(page.querySelector('.cast__row--selected')?.getAttribute('aria-current')).toBe('page');
    const detailEl = page.querySelector('r2m-character-detail')!;
    await detailEl.rendered();
    expect(detailEl.textContent).toContain('Paul');
    expect(api.calls('GET', `${BASE}/characters/paul/lines`)).toHaveLength(1);
  });

  it('an unknown id says the character was not found and links back', async () => {
    const { page } = await render('projects/dune/cast/ghost');
    api.on('GET', `${BASE}/characters/ghost/lines`, []);
    await settle();
    await page.rendered();
    expect(text(page.querySelector('.cast__detail'))).toContain('Character not found');
    expect(page.querySelector('.cast__detail a')?.getAttribute('href')).toBe('projects/dune/cast');
  });

  it('an empty roster shows the empty state', async () => {
    const { page } = await render('projects/dune/cast', []);
    expect(text(page.querySelector('.cast__list'))).toContain('No characters yet');
  });

  it('Add character prompts for a name, posts CreateCharacter and opens the new one', async () => {
    const { page } = await render();
    api.on('POST', COMMANDS, { outcome: 'Committed', newEntityId: 'stilgar' });
    page.querySelector<HTMLButtonElement>('[data-action="add-character"]')!.click();
    await settle();
    const prompt = document.querySelector('r2m-text-prompt-dialog')!;
    await prompt.rendered();
    const input = prompt.querySelector<HTMLInputElement>('.r2m-text-prompt-dialog__input')!;
    input.value = 'Stilgar';
    input.dispatchEvent(new Event('input'));
    await prompt.rendered();
    prompt.querySelector<HTMLButtonElement>('.r2m-text-prompt-dialog__confirm')!.click();
    await settle();
    await settle();
    expect(api.calls('POST', COMMANDS).map((r) => r.body)).toEqual([
      { type: 'CreateCharacter', name: 'Stilgar' },
    ]);
    expect(navigation.calls.at(-1)?.url).toBe('http://localhost/app2/projects/dune/cast/stilgar');
  });

  it('the toolbar is off while a voice batch runs', async () => {
    const { page } = await render();
    const actions = ['discover', 'generate-prompts', 'generate-audio', 'add-character'];
    const disabled = () =>
      actions.map((a) => page.querySelector<HTMLButtonElement>(`[data-action="${a}"]`)!.disabled);
    expect(disabled()).toEqual([false, false, false, false]);
    liveFake.voiceBatch.set({ ...IDLE_VOICE_BATCH, isRunning: true });
    await page.rendered();
    expect(disabled()).toEqual([true, true, true, true]);
  });

  it('Discover is gated by preflight and opens the discovery dialog over the roster', async () => {
    const { page } = await render();
    preflightAnswer = false;
    page.querySelector<HTMLButtonElement>('[data-action="discover"]')!.click();
    await settle();
    expect(preflightTasks).toEqual(['discovery']);
    expect(document.querySelector('r2m-discovery-dialog')).toBeNull();

    preflightAnswer = true;
    api.on('POST', `${BASE}/characters/discover`, {
      status: 'Ok',
      reason: null,
      characters: [{ name: 'Stilgar', aliases: [], existingCharacterId: null }],
      collisions: [],
    });
    api.on('POST', `${BASE}/characters/discover/apply`, { applied: 1 });
    page.querySelector<HTMLButtonElement>('[data-action="discover"]')!.click();
    await settle();
    const dialog = document.querySelector('r2m-discovery-dialog')!;
    expect(dialog.data.roster.map((r) => r.name)).toEqual(['Narrator', 'Paul', 'Jessica']);
    await settle();
    await dialog.rendered();
    api.on('GET', SUMMARY, [...ROWS, row('Stilgar')]);
    dialog.querySelector<HTMLButtonElement>('[data-action="apply"]')!.click();
    await settle();
    await settle();
    await page.rendered();
    expect(toasts).toEqual(['1 character added']);
    expect(names(page)).toContain('Stilgar');
  });

  it('?discover=1 opens discovery once the roster is up and drops the param', async () => {
    api.on('POST', `${BASE}/characters/discover`, () => new Promise(() => undefined));
    const { page } = await render('projects/dune/cast?discover=1');
    await settle();
    expect(preflightTasks).toEqual(['discovery']);
    expect(document.querySelector('r2m-discovery-dialog')).not.toBeNull();
    const last = navigation.calls.at(-1)!;
    expect(last.url).toBe('http://localhost/app2/projects/dune/cast');
    expect(last.history).toBe('replace');
    expect(page.isConnected).toBe(true);
  });

  it('Generate voice prompts starts the batch at once without voices and opens the drawer', async () => {
    const { page } = await render('projects/dune/cast', [row('Narrator'), row('Jessica')]);
    api.on('POST', `${BASE}/voice-batch/prompts`, { started: true });
    page.querySelector<HTMLButtonElement>('[data-action="generate-prompts"]')!.click();
    await settle();
    await settle();
    expect(document.querySelector('r2m-voice-scope-dialog')).toBeNull();
    expect(preflightTasks).toEqual(['voicePrompt']);
    expect(api.calls('POST', `${BASE}/voice-batch/prompts`).map((r) => r.body)).toEqual([
      { regenerateAll: false },
    ]);
    expect(drawerOpen()).toBe(true);
  });

  it('with voices the scope dialog comes first; Cancel starts nothing', async () => {
    const { page } = await render();
    page.querySelector<HTMLButtonElement>('[data-action="generate-prompts"]')!.click();
    await settle();
    const scope = document.querySelector('r2m-voice-scope-dialog')!;
    await scope.rendered();
    Array.from(scope.querySelectorAll('button'))
      .find((b) => b.textContent?.trim() === 'Cancel')!
      .click();
    await settle();
    expect(preflightTasks).toEqual([]);
    expect(api.calls('POST', `${BASE}/voice-batch/prompts`)).toHaveLength(0);
  });

  it('Generate audio is gated by preflight and toasts a refused start', async () => {
    const { page } = await render();
    api.on('POST', `${BASE}/voice-batch/audio`, () =>
      Response.json(
        { title: 'Conflict', status: 409, detail: 'A batch is running' },
        { status: 409 },
      ),
    );
    page.querySelector<HTMLButtonElement>('[data-action="generate-audio"]')!.click();
    await settle();
    await settle();
    expect(preflightTasks).toEqual(['voiceDesign']);
    expect(toasts).toEqual(['problem: A batch is running']);
    expect(drawerOpen()).toBe(false);
  });

  it('the banner links a narrator through SetNarratorCharacter', async () => {
    const { page } = await render();
    api.on('POST', COMMANDS, { outcome: 'Committed' });
    const banner = page.querySelector('r2m-narrator-banner')!;
    banner.pick('paul');
    await settle();
    expect(api.calls('POST', COMMANDS).map((r) => r.body)).toEqual([
      { type: 'SetNarratorCharacter', characterId: 'paul' },
    ]);
  });

  it('a linked narrator names the seed row and shows the signpost in its place', async () => {
    const linkedRows = [row('Narrator'), row('Paul', { narratesBook: true }), row('Jessica')];
    const { page } = await render('projects/dune/cast/narrator', linkedRows, true, {
      narrator: ['Narrator Voice'],
    });
    await settle();
    await page.rendered();
    expect(names(page)).toEqual(['Narrator → Paul', 'Jessica', 'Paul']);
    const signpost = page.querySelector('[data-testid="narrator-signpost"]')!;
    expect(text(signpost)).toContain('Narrator → Paul');
    expect(text(signpost)).toContain('1 unused narrator voice');
    expect(text(signpost)).toContain('Narrator Voice');
    expect(page.querySelector('r2m-character-detail')).toBeNull();

    Array.from(signpost.querySelectorAll('button'))
      .find((b) => b.textContent?.includes('Go to Paul'))!
      .click();
    expect(navigation.calls.at(-1)?.url).toBe('http://localhost/app2/projects/dune/cast/paul');
  });

  it('a failed roster load shows the error chip', async () => {
    api.on('GET', BASE, detail()).on('GET', `${BASE}/status`, STATUS);
    api.on('GET', SUMMARY, () =>
      Response.json({ title: 'Error', status: 500, detail: 'db locked' }, { status: 500 }),
    );
    navigation = installNavigation('projects/dune/cast');
    const router = new Router();
    override(Router, router);
    router.start(ROUTES, async () => true);
    const outlet = document.createElement('r2m-outlet');
    document.body.append(outlet);
    for (let i = 0; i < 4; i++) await settle();
    const page = outlet.querySelector<CastPage>('r2m-cast-page')!;
    await page.rendered();
    await page.rendered();
    expect(text(page.querySelector('.r2m-status-chip--error'))).toContain(
      'Cast could not be loaded: db locked',
    );
  });
});
