import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AssemblyOutputDto, ProjectDetailDto, ProjectStatusDto } from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import type { AssemblyMessage, AssemblyState } from '@app/live/live-messages';
import { IDLE_ASSEMBLY } from '@app/live/live-state';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { installNavigation, settle } from '../../../testing/fake-navigation';
import { type ExportPage, formatOutputDate } from './export-page';
import '../project/project-shell';

const BASE = '/api/projects/dune';
const START_URL = `${BASE}/assembly`;
const OUTPUTS_URL = `${BASE}/assembly/outputs`;
const CANCEL_URL = '/api/assembly/cancel';
const SETTINGS_URL = '/api/settings/audio-processing';

const DETAIL: ProjectDetailDto = {
  folderName: 'dune',
  title: 'Dune',
  bookTitle: 'Dune',
  author: 'Frank Herbert',
  filename: 'dune.epub',
  fileType: 'Epub',
  coverImage: null,
  narratorOnlyMode: false,
  narrator: { characterId: 'n', displayName: 'Narrator', isLinked: false },
};

const STATUS: ProjectStatusDto = {
  hasContent: true,
  characters: 1,
  charactersWithLines: 1,
  readyVoices: 1,
  items: { total: 8, withAudio: 5, unattributed: 0 },
  attribution: { remaining: 0, processing: false, queued: 0 },
  audio: { remaining: 2 },
  review: 0,
  volumeIds: [],
  nodes: {},
  revision: 1,
};

const OUTPUTS: AssemblyOutputDto[] = [
  {
    fileName: 'Dune_partial_20260919.m4b',
    sizeBytes: 1536,
    createdAt: '2026-09-19T10:00:00+00:00',
    isPartial: true,
  },
  {
    fileName: 'Dune.m4b',
    sizeBytes: 2048,
    createdAt: '2026-09-01T10:00:00+00:00',
    isPartial: false,
  },
];

/** The host's refusal of a full build while items lack audio. */
const missingAudio = (count: number) =>
  Response.json(
    {
      title: 'Conflict',
      status: 409,
      detail: `${count} items still need audio.`,
      audioRemainingCount: count,
    },
    { status: 409 },
  );

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: 'export', tag: 'r2m-export-page' }],
  },
];

let api: FakeApi;
let live: FakeLive;
let confirms: ConfirmOptions[];
let confirmAnswer: boolean;
let toasts: string[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  confirms = [];
  confirmAnswer = true;
  toasts = [];
  override(ConfirmService, {
    confirm: async (o: ConfirmOptions) => {
      confirms.push(o);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
  override(ToastService, {
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
});
afterEach(() => document.body.replaceChildren());

/** The export page under the project shell, through a root outlet as in the app. */
async function render(outputs: AssemblyOutputDto[] | Response = OUTPUTS) {
  api.on('GET', BASE, DETAIL).on('GET', `${BASE}/status`, STATUS);
  api.on('GET', OUTPUTS_URL, outputs);
  api.on('GET', SETTINGS_URL, { ffmpegPath: 'D:\\ffmpeg\\ffmpeg.exe' });
  installNavigation('projects/dune/export');
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<ExportPage>('r2m-export-page');
  if (!page) throw new Error('the export page did not render');
  await page.rendered();
  await page.rendered();
  return page;
}

/** As `LiveService` does: fold the state, then fan the message out. */
async function pushAssembly(page: ExportPage, state: Partial<AssemblyState>, m: AssemblyMessage) {
  live.assembly.set({ ...IDLE_ASSEMBLY, ...state });
  live.emit('assembly', m);
  for (let i = 0; i < 3; i++) await settle();
  await page.rendered();
}

const button = (el: Element, action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
/** Visible text, without the icon ligature names. */
const text = (el: Element | null) => {
  if (!el) return '';
  const copy = el.cloneNode(true) as Element;
  copy.querySelectorAll('.r2m-icon').forEach((n) => n.remove());
  return copy.textContent?.replace(/\s+/g, ' ').trim() ?? '';
};
const states = (page: ExportPage) =>
  Array.from(page.querySelectorAll('[data-phase]'), (n) => n.getAttribute('data-state'));

async function click(page: ExportPage, action: string): Promise<void> {
  button(page, action).click();
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
}

describe('formatOutputDate', () => {
  it("renders Angular's medium date and time in en-US", () => {
    // Built from local components so the expectation holds in any time zone.
    const local = new Date(2026, 8, 19, 10, 0, 0).toISOString();
    expect(formatOutputDate(local)).toBe('Sep 19, 2026, 10:00:00 AM');
  });
});

describe('r2m-export-page', () => {
  it('shows readiness and the outputs with size, partial chip and a download link', async () => {
    const page = await render();

    const readiness = text(page.querySelector('[data-card="readiness"]'));
    expect(readiness).toContain('5 of 8');
    expect(readiness).toContain('D:\\ffmpeg\\ffmpeg.exe');
    expect(text(button(page, 'assemble'))).toBe('Assemble…');
    expect(button(page, 'assemble').disabled).toBe(false);

    const rows = page.querySelectorAll('[data-output]');
    expect(rows.length).toBe(2);
    expect(text(rows[0]!)).toContain('1.5 KB');
    expect(text(rows[0]!)).toContain('Partial');
    expect(text(rows[1]!)).not.toContain('Partial');
    expect(rows[1]!.querySelector('a[data-action="download-output"]')!.getAttribute('href')).toBe(
      '/api/projects/dune/assembly/outputs/Dune.m4b',
    );
    expect(page.querySelector('[data-card="progress"]')).toBeNull();
  });

  it('says there are no audiobooks yet once the empty list has arrived', async () => {
    const page = await render([]);
    expect(text(page.querySelector('[data-card="outputs"] .r2m-empty-state'))).toContain(
      'No audiobooks yet',
    );
  });

  it('turns the missing-audio 409 into the partial prompt and then starts with allowPartial', async () => {
    api.on('POST', START_URL, (body) =>
      (body as { allowPartial: boolean }).allowPartial ? { started: true } : missingAudio(3),
    );
    const page = await render();

    await click(page, 'assemble');

    expect(api.calls('POST', START_URL).map((r) => r.body)).toEqual([
      { allowPartial: false },
      { allowPartial: true },
    ]);
    expect(confirms).toHaveLength(1);
    expect(confirms[0]!.destructive).toBe(true);
    expect(confirms[0]!.confirmLabel).toBe('Assemble partial — 3 items missing audio');
    expect(toasts).toEqual([]);
  });

  it('starts nothing when the partial prompt is declined', async () => {
    api.on('POST', START_URL, () => missingAudio(1));
    confirmAnswer = false;
    const page = await render();

    await click(page, 'assemble');

    expect(api.calls('POST', START_URL).map((r) => r.body)).toEqual([{ allowPartial: false }]);
    expect(toasts).toEqual([]);
    expect(button(page, 'assemble').disabled).toBe(false);
  });

  it('toasts the other 409 (a run is already active) instead of offering a partial build', async () => {
    api.on('POST', START_URL, () =>
      Response.json(
        { title: 'Conflict', status: 409, detail: 'Assembly is already running.' },
        { status: 409 },
      ),
    );
    const page = await render();

    await click(page, 'assemble');

    expect(confirms).toEqual([]);
    expect(toasts).toEqual(['problem: Assembly is already running.']);
  });

  it('deletes an output after a confirm and reloads the list', async () => {
    const page = await render();
    api.on('DELETE', `${OUTPUTS_URL}/Dune.m4b`, undefined);
    api.on('GET', OUTPUTS_URL, [OUTPUTS[0]]);

    page.querySelectorAll<HTMLButtonElement>('[data-action="delete-output"]')[1]!.click();
    for (let i = 0; i < 4; i++) await settle();
    await page.rendered();

    expect(confirms[0]!.title).toBe('Delete this audiobook?');
    expect(api.calls('DELETE', `${OUTPUTS_URL}/Dune.m4b`)).toHaveLength(1);
    expect(page.querySelectorAll('[data-output]').length).toBe(1);
  });

  it('walks the phase stepper off the hub, shows encode progress and offers Cancel', async () => {
    api.on('POST', CANCEL_URL, undefined);
    const page = await render([]);

    const running = { isRunning: true, folder: 'dune' };
    await pushAssembly(
      page,
      { ...running, currentPhase: 'Gather' },
      { kind: 'phaseStarted', phase: 'Gather', folder: 'dune' },
    );
    expect(states(page)).toEqual(['active', 'pending', 'pending', 'pending', 'pending']);
    expect(page.querySelector('[data-phase="Gather"]')!.getAttribute('aria-current')).toBe('step');
    expect(button(page, 'assemble').disabled).toBe(true);
    expect(page.querySelector('[data-role="outcome"]')).toBeNull();

    await pushAssembly(
      page,
      { ...running, currentPhase: 'Encode' },
      { kind: 'phaseStarted', phase: 'Encode', folder: 'dune' },
    );
    await pushAssembly(
      page,
      { ...running, currentPhase: 'Encode', encodePercent: 50 },
      { kind: 'progress', fraction: 0.5, folder: 'dune' },
    );
    expect(states(page)).toEqual(['done', 'done', 'done', 'active', 'pending']);
    expect(text(page.querySelector('[data-role="encode-percent"]'))).toBe('50%');
    expect(page.querySelector('progress')!.getAttribute('value')).toBe('0.5');

    await click(page, 'cancel-assembly');
    expect(api.calls('POST', CANCEL_URL)).toHaveLength(1);

    await pushAssembly(
      page,
      { folder: 'dune', currentPhase: 'Encode' },
      { kind: 'cancelled', folder: 'dune' },
    );
    expect(states(page)).toEqual(['done', 'done', 'done', 'cancelled', 'pending']);
    expect(text(page.querySelector('[data-role="outcome"]'))).toBe('Cancelled');
    expect(page.querySelector('[data-action="cancel-assembly"]')).toBeNull();
    expect(button(page, 'assemble').disabled).toBe(false);
  });

  it('reports the finished file and reloads the outputs when the run completes', async () => {
    const page = await render([]);
    await pushAssembly(
      page,
      { isRunning: true, folder: 'dune', currentPhase: 'Finalize' },
      { kind: 'phaseStarted', phase: 'Finalize', folder: 'dune' },
    );
    api.on('GET', OUTPUTS_URL, [OUTPUTS[1]]);

    await pushAssembly(
      page,
      { folder: 'dune', outputFileName: 'Dune.m4b' },
      { kind: 'completed', folder: 'dune', outputFileName: 'Dune.m4b' },
    );

    expect(states(page)).toEqual(Array(5).fill('done'));
    expect(text(page.querySelector('[data-role="outcome"]'))).toBe('Finished: Dune.m4b');
    expect(page.querySelectorAll('[data-output]').length).toBe(1);
  });

  it('shows the last line of a failure and keeps the whole error to read', async () => {
    const page = await render([]);
    const stderr = 'ffmpeg version 7.1\nError opening input';
    await pushAssembly(
      page,
      { isRunning: true, folder: 'dune', currentPhase: 'Encode' },
      { kind: 'phaseStarted', phase: 'Encode', folder: 'dune' },
    );
    await pushAssembly(
      page,
      { folder: 'dune', currentPhase: 'Encode', lastError: stderr },
      { kind: 'failed', reason: stderr, folder: 'dune' },
    );

    expect(states(page)).toEqual(['done', 'done', 'done', 'failed', 'pending']);
    expect(text(page.querySelector('[data-role="outcome"]'))).toBe('Failed: Error opening input');
    expect(page.querySelector('pre.export__error')!.textContent).toBe(stderr);
  });

  it('after a reload reads how the last run ended off the hub snapshot', async () => {
    live.assembly.set({ ...IDLE_ASSEMBLY, folder: 'dune', outputFileName: 'Dune.m4b' });
    const page = await render();

    expect(text(page.querySelector('[data-role="outcome"]'))).toBe('Finished: Dune.m4b');
    expect(states(page)).toEqual(Array(5).fill('done'));
  });

  it("disables Assemble while another project's run is going, and ignores its messages", async () => {
    live.assembly.set({
      ...IDLE_ASSEMBLY,
      isRunning: true,
      folder: 'foundation',
      currentPhase: 'Encode',
    });
    const page = await render();

    expect(button(page, 'assemble').disabled).toBe(true);
    expect(text(page.querySelector('[data-card="readiness"] .r2m-status-chip'))).toContain(
      'Another project is being assembled: foundation',
    );
    expect(page.querySelector('[data-card="progress"]')).toBeNull();

    await pushAssembly(
      page,
      { folder: 'foundation', outputFileName: 'Foundation.m4b' },
      { kind: 'completed', folder: 'foundation', outputFileName: 'Foundation.m4b' },
    );
    expect(page.querySelector('[data-card="progress"]')).toBeNull();
    expect(button(page, 'assemble').disabled).toBe(false);
  });
});
