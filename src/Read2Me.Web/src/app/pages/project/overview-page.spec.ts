import { afterEach, beforeEach, describe, expect, it, mock } from 'bun:test';
import type { AssemblyOutputDto, ProjectDetailDto, ProjectStatusDto } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { Preflight } from '@app/shared/preflight';
import { type ConfirmOptions, ConfirmService } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { type FakeNavigation, installNavigation, settle } from '../../../testing/fake-navigation';
import type { OverviewPage } from './overview-page';
import './project-shell';
import { formatBuildDate } from './project-store';
import './overview-page';

const BASE = '/api/projects/dune';

const DETAIL: ProjectDetailDto = {
  folderName: 'dune',
  title: 'Dune',
  bookTitle: 'Dune',
  author: 'Frank Herbert',
  filename: 'dune.epub',
  fileType: 'Epub',
  coverImage: null,
  narratorOnlyMode: true,
  narrator: { characterId: 'w', displayName: 'Watson', isLinked: true },
};

function status(overrides: Partial<ProjectStatusDto> = {}): ProjectStatusDto {
  return {
    hasContent: true,
    characters: 2,
    charactersWithLines: 2,
    readyVoices: 1,
    items: { total: 8, withAudio: 2, unattributed: 3 },
    attribution: { remaining: 3, processing: false, queued: 0 },
    audio: { remaining: 4 },
    review: 0,
    volumeIds: ['v1', 'v2'],
    nodes: {
      v1: {
        attributionRemaining: 2,
        audioRemaining: 2,
        review: 0,
        attributionProcessing: false,
        attributionQueued: 0,
        isDone: false,
      },
      v2: {
        attributionRemaining: 1,
        audioRemaining: 2,
        review: 0,
        attributionProcessing: false,
        attributionQueued: 0,
        isDone: false,
      },
    },
    pauseParagraphs: 0,
    checkpoints: {},
    revision: 1,
    ...overrides,
  };
}

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: '', tag: 'r2m-overview-page' }],
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
    warn: (m: string) => toasts.push(m),
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** The overview under the project shell, through a root outlet as in the app. */
async function render(s = status(), outputs: AssemblyOutputDto[] = []) {
  api.on('GET', BASE, DETAIL).on('GET', `${BASE}/status`, s);
  api.on('GET', `${BASE}/assembly/outputs`, outputs);
  navigation = installNavigation('projects/dune');
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<OverviewPage>('r2m-overview-page');
  if (!page) throw new Error('the overview page did not render');
  await page.rendered();
  return page;
}

const button = (el: Element, action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

describe('r2m-overview-page', () => {
  it('shows the stepper and the project card side by side', async () => {
    const page = await render();
    expect(page.querySelectorAll('.r2m-pipeline__step').length).toBe(9);
    expect(text(page.querySelector('[data-step="speakers"]'))).toContain('3 paragraphs unassigned');
    const details = page.querySelector('r2m-project-details-panel')!;
    expect(details.textContent).toContain('Narrated by Watson');
    expect(details.textContent).toContain('dune.epub');
    expect(details.querySelector('a.details__cast-link')?.getAttribute('href')).toBe(
      'projects/dune/cast/w',
    );
    expect(details.querySelector<HTMLInputElement>('input[name=narratorOnly]')?.checked).toBe(true);
  });

  it('Remove cover asks a red confirm first (guidance-primitives 14), then DELETEs and refetches', async () => {
    api
      .on('GET', BASE, { ...DETAIL, coverImage: 'cover.jpg' })
      .on('GET', `${BASE}/status`, status());
    api.on('GET', `${BASE}/assembly/outputs`, []);
    api.on('DELETE', `${BASE}/cover`, undefined);
    navigation = installNavigation('projects/dune');
    const router = new Router();
    override(Router, router);
    router.start(ROUTES, async () => true);
    const outlet = document.createElement('r2m-outlet');
    document.body.append(outlet);
    for (let i = 0; i < 4; i++) await settle();
    const details = outlet.querySelector('r2m-project-details-panel')!;
    await details.rendered();
    const remove = () => details.querySelector<HTMLButtonElement>('.details__remove-cover')!;

    confirmAnswer = false;
    remove().click();
    for (let i = 0; i < 4; i++) await settle();
    expect(confirms).toEqual([
      expect.objectContaining({
        title: 'Remove cover?',
        message: expect.stringContaining('Deletes the cover image.'),
        confirmLabel: 'Remove cover',
        destructive: true,
      }),
    ]);
    expect(api.calls('DELETE', `${BASE}/cover`)).toHaveLength(0);

    confirmAnswer = true;
    api.on('GET', BASE, DETAIL);
    remove().click();
    for (let i = 0; i < 6; i++) await settle();
    expect(api.calls('DELETE', `${BASE}/cover`)).toHaveLength(1);
    await details.rendered();
    expect(remove()).toBeNull();
  });

  it('a fresh project offers Read book as the next step and imports then refetches status', async () => {
    const page = await render(
      status({
        hasContent: false,
        volumeIds: [],
        nodes: {},
        items: { total: 0, withAudio: 0, unattributed: 0 },
      }),
    );
    expect(page.querySelector('[aria-current="step"]')?.getAttribute('data-step')).toBe('import');
    api.on('POST', `${BASE}/import`, undefined).on('GET', `${BASE}/status`, status());

    button(page, 'readBook').click();
    await settle();
    await page.rendered();

    expect(api.calls('POST', `${BASE}/import`).map((r) => r.body)).toEqual([{ reread: false }]);
    expect(api.calls('GET', `${BASE}/status`)).toHaveLength(2);
    expect(text(page.querySelector('[data-step="import"]'))).toContain('Done');
    expect(toasts).toEqual(['Book read in']);
  });

  it('Attribute unprocessed enqueues every volume with unprocessedOnly, in book order', async () => {
    const page = await render();
    api.on('POST', `${BASE}/attribution/enqueue`, { enqueued: 2 });

    button(page, 'attribute').click();
    await settle();

    expect(ensureReady).toHaveBeenCalledWith('attribution');
    expect(api.calls('POST', `${BASE}/attribution/enqueue`).map((r) => r.body)).toEqual([
      { level: 'volume', nodeId: 'v1', unprocessedOnly: true },
      { level: 'volume', nodeId: 'v2', unprocessedOnly: true },
    ]);
    expect(api.calls('GET', `${BASE}/status`)).toHaveLength(2);
    expect(toasts).toEqual(['Queued 4 paragraphs']);
  });

  it('a nodeStatus delta taking attribution to 0 unassigned leaves Speakers waiting for its review', async () => {
    const page = await render();
    const step = (name: string) => page.querySelector<HTMLElement>(`[data-step="${name}"]`)!;
    const chip = (name: string) => text(step(name).querySelector('.r2m-pipeline__chip'));
    expect(step('structure').getAttribute('aria-current')).toBe('step');
    expect(chip('speakers')).toContain('3 paragraphs unassigned');

    const attributed = {
      attributionRemaining: 0,
      audioRemaining: 2,
      review: 0,
      attributionProcessing: false,
      attributionQueued: 0,
      isDone: false,
    };
    live.emit('nodeStatus', {
      folder: 'dune',
      nodes: { v1: attributed, v2: attributed },
      folderAudioRemaining: 4,
    });
    await page.rendered();

    expect(chip('speakers')).toContain('Review not confirmed');
    expect(button(page, 'attribute').disabled).toBe(true);
    expect(button(page, 'attribute').closest('.r2m-blockable')?.getAttribute('data-tooltip')).toBe(
      'Every paragraph has a speaker',
    );
    expect(step('structure').getAttribute('aria-current')).toBe('step');
  });

  it('a cancelled preflight queues nothing', async () => {
    const page = await render();
    ensureReady.mockImplementation(async () => false);
    api.on('POST', `${BASE}/attribution/enqueue`, { enqueued: 2 });

    button(page, 'attribute').click();
    await settle();

    expect(api.calls('POST', `${BASE}/attribution/enqueue`)).toHaveLength(0);
    expect(toasts).toEqual([]);
  });

  it('Generate needed audio enqueues every volume with needsAudioOnly and the narrator-only policy', async () => {
    const page = await render();
    api.on('POST', `${BASE}/audio/enqueue`, { enqueued: 1 });

    button(page, 'generateAudio').click();
    await settle();

    expect(ensureReady).toHaveBeenCalledWith('audio');
    expect(api.calls('POST', `${BASE}/audio/enqueue`).map((r) => r.body)).toEqual([
      { level: 'volume', nodeId: 'v1', needsAudioOnly: true, narratorOnlyMode: true },
      { level: 'volume', nodeId: 'v2', needsAudioOnly: true, narratorOnlyMode: true },
    ]);
    expect(toasts).toEqual(['Queued 2 items']);
  });

  it('Reread asks for a destructive confirm before posting reread: true', async () => {
    const page = await render();
    api.on('POST', `${BASE}/import`, undefined);

    confirmAnswer = false;
    button(page, 'reread').click();
    for (let i = 0; i < 4; i++) await settle();
    expect(confirms).toEqual([
      expect.objectContaining({ title: 'Reread the book?', destructive: true }),
    ]);
    expect(confirms[0]?.message).not.toContain('confirmed and skipped steps');
    expect(api.calls('POST', `${BASE}/import`)).toHaveLength(0);

    confirmAnswer = true;
    button(page, 'reread').click();
    for (let i = 0; i < 4; i++) await settle();
    expect(api.calls('POST', `${BASE}/import`).map((r) => r.body)).toEqual([{ reread: true }]);
    expect(toasts).toEqual(['Book reread']);
  });

  it('Reread names the confirmed and skipped steps it clears when a checkpoint row exists (guidance-primitives 14)', async () => {
    const page = await render(
      status({
        checkpoints: { structure: { state: 'confirmed', stale: false, at: '2026-10-18' } },
      }),
    );
    confirmAnswer = false;
    button(page, 'reread').click();
    for (let i = 0; i < 4; i++) await settle();
    expect(confirms[0]?.message).toContain('Clears your confirmed and skipped steps.');
  });

  it('navigation actions route to cast, the Book page (modes and ?action= deep links) and export', async () => {
    // Each navigation leaves the overview, so every action starts from a fresh page.
    const routes: [string, string][] = [
      ['checkStructure', 'projects/dune/book?mode=read'],
      ['manualReread', 'projects/dune/book?action=manual-reread'],
      ['openSpeakers', 'projects/dune/book?mode=speakers'],
      ['openCast', 'projects/dune/cast'],
      ['reviewText', 'projects/dune/book?mode=read'],
      ['editWithAi', 'projects/dune/book?action=edit-ai'],
      ['addTitles', 'projects/dune/book?action=titles'],
      ['addPauses', 'projects/dune/book'],
      ['openAudioMode', 'projects/dune/book?mode=audio'],
      ['assemble', 'projects/dune/export'],
    ];
    for (const [action, expected] of routes) {
      document.body.replaceChildren();
      const page = await render();
      button(page, action).click();
      expect(navigation.calls.at(-1)?.url).toBe(`http://localhost/app/${expected}`);
    }
  });

  it('editing the title inline PATCHes and shows the returned detail', async () => {
    const page = await render();
    api.on('PATCH', BASE, { ...DETAIL, title: 'Dune Messiah' });
    const edit = page.querySelector<HTMLButtonElement>(
      '.details__title .r2m-inline-edit__display',
    )!;
    edit.click();
    await page.rendered();
    const input = page.querySelector<HTMLInputElement>('.details__title input')!;
    input.value = 'Dune Messiah';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await settle();
    await page.rendered();

    expect(api.calls('PATCH', BASE).map((r) => r.body)).toEqual([{ title: 'Dune Messiah' }]);
    expect(page.querySelector('.r2m-page-header__title')?.textContent).toBe('Dune Messiah');
  });

  it('the narrator-only switch PUTs the policy and refetches the detail', async () => {
    const page = await render();
    api.on('PUT', `${BASE}/narrator-only-mode`, undefined);
    const toggle = page.querySelector<HTMLInputElement>('input[name=narratorOnly]')!;
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    await settle();

    expect(api.calls('PUT', `${BASE}/narrator-only-mode`).map((r) => r.body)).toEqual([
      { enabled: false },
    ]);
    expect(api.calls('GET', BASE)).toHaveLength(2);
  });

  it('a refused narrator-only toggle snaps back to what the host holds', async () => {
    const page = await render();
    api.on('PUT', `${BASE}/narrator-only-mode`, () => problem(409, 'policy locked'));
    const toggle = page.querySelector<HTMLInputElement>('input[name=narratorOnly]')!;
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    await settle();
    await page.rendered();

    expect(toasts).toEqual(['problem: policy locked']);
    expect(api.calls('GET', BASE)).toHaveLength(2);
    expect(page.querySelector<HTMLInputElement>('input[name=narratorOnly]')?.checked).toBe(true);
  });

  it('shows the newest build on the export step', async () => {
    const page = await render(status(), [
      { fileName: 'dune.m4b', sizeBytes: 1, createdAt: '2026-09-19T10:00:00Z', isPartial: false },
    ]);
    const exportStep = page.querySelector('[data-step="export"]')!;
    expect(text(exportStep)).toContain(`Last build: ${formatBuildDate('2026-09-19T10:00:00Z')}`);
    expect(exportStep.classList.contains('r2m-pipeline__step--done')).toBe(true);
  });
});
