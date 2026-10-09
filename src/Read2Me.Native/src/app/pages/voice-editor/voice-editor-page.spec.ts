import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type {
  PreviewResponse,
  ProjectDetailDto,
  ProjectStatusDto,
  StepCatalogEntryDto,
  VoiceDto,
} from '@app/api';
import { Router, type RouteDef } from '@app/core/router';
import { override, resetServices } from '@app/core/services';
import { ConfirmService } from '@app/ui/dialogs';
import { FakeApi, problem } from '../../../testing/fake-api';
import { FakeLive } from '../../../testing/fake-live';
import { installNavigation, settle } from '../../../testing/fake-navigation';
import type { VoiceEditorPage } from './voice-editor-page';
import '../project/project-shell';
import './voice-editor-page';

const BASE = '/api/projects/dune';
const VOICE_URL = `${BASE}/voices/v1`;
const CATALOG_URL = '/api/audio/steps/catalog';
const PREVIEW_URL = `${VOICE_URL}/editor/preview`;
const APPLY_URL = `${VOICE_URL}/editor/apply`;
const RESTORE_URL = `${VOICE_URL}/editor/restore`;

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
  items: { total: 1, withAudio: 0, unattributed: 0 },
  attribution: { remaining: 0, processing: false, queued: 0 },
  audio: { remaining: 1 },
  review: 0,
  volumeIds: [],
  nodes: {},
  revision: 1,
};

const CATALOG: StepCatalogEntryDto[] = [
  {
    stepId: 'denoise',
    label: 'Denoise',
    blurb: 'Removes broadband room noise and hum.',
    dials: [
      {
        key: 'strength',
        label: 'Strength',
        kind: 'number',
        min: 1,
        max: 1000,
        step: 1,
        default: 20,
        nullable: false,
      },
    ],
    defaults: { strength: 20 },
  },
  {
    stepId: 'hiss-reduce',
    label: 'Hiss reduce',
    blurb: 'Attenuates hiss above 5 kHz only.',
    dials: [
      {
        key: 'preset',
        label: 'Strength',
        kind: 'enum',
        options: [{ value: 'light' }, { value: 'strong' }],
        default: 'light',
        nullable: false,
      },
    ],
    defaults: { preset: 'light' },
  },
  {
    stepId: 'silence-trim',
    label: 'Silence trim',
    blurb: 'Trims dead air from the start and end.',
    dials: [
      {
        key: 'thresholdDb',
        label: 'Threshold (dB)',
        kind: 'number',
        min: -60,
        max: -35,
        step: 1,
        default: -35,
        nullable: false,
      },
    ],
    defaults: { thresholdDb: -35 },
  },
];

const VOICE: VoiceDto = {
  id: 'v1',
  characterId: 'alice',
  name: 'Main',
  description: null,
  source: 'Uploaded',
  designPrompt: null,
  transcript: null,
  audioFileName: 'voices/alice/v1-main.wav',
  isEdited: false,
  voiceDesignSettingsOverrideJson: null,
  ttsSettingsOverrideJson: null,
  referenceSeconds: null,
  referenceWarning: null,
};

const PREVIEW: PreviewResponse = {
  previewId: 'p1',
  stages: [
    { stepId: 'denoise', applied: true, reason: null, url: '/api/previews/p1/denoise.wav' },
    {
      stepId: 'silence-trim',
      applied: false,
      reason: 'trimmed result under 1000 ms',
      url: '/api/previews/p1/silence-trim.wav',
    },
  ],
};

const ROUTES: RouteDef[] = [
  {
    path: 'projects/:folder',
    tag: 'r2m-project-shell',
    children: [{ path: 'voices/:voiceId/editor', tag: 'r2m-voice-editor-page' }],
  },
];

let api: FakeApi;
let confirms: string[];
let confirmAnswer: boolean;

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  new FakeLive().install();
  confirms = [];
  confirmAnswer = true;
  override(ConfirmService, {
    confirm: async (o: { title: string }) => {
      confirms.push(o.title);
      return confirmAnswer;
    },
  } as unknown as ConfirmService);
});
afterEach(() => document.body.replaceChildren());

/** The editor under the project shell, through a root outlet as in the app. */
async function render(voice: VoiceDto | Response = VOICE) {
  api.on('GET', BASE, DETAIL).on('GET', `${BASE}/status`, STATUS);
  api.on('GET', VOICE_URL, voice);
  api.on('GET', CATALOG_URL, CATALOG);
  installNavigation('projects/dune/voices/v1/editor');
  const router = new Router();
  override(Router, router);
  router.start(ROUTES, async () => true);
  const outlet = document.createElement('r2m-outlet');
  document.body.append(outlet);
  for (let i = 0; i < 4; i++) await settle();
  const page = outlet.querySelector<VoiceEditorPage>('r2m-voice-editor-page');
  if (!page) throw new Error('the voice editor page did not render');
  await page.rendered();
  await page.rendered();
  return page;
}

const button = (el: Element, action: string) =>
  el.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

async function tick(page: VoiceEditorPage, stepId: string): Promise<void> {
  page.querySelector<HTMLInputElement>(`[data-tick="${stepId}"]`)!.click();
  await settle();
  await page.rendered();
}

/** Preview with the seeded dials, and check the request carried exactly them. */
async function previewDefaultChain(page: VoiceEditorPage): Promise<void> {
  button(page, 'preview').click();
  for (let i = 0; i < 3; i++) await settle();
  await page.rendered();
  const [req] = api.calls('POST', PREVIEW_URL);
  expect(req?.body).toEqual({
    steps: [
      { stepId: 'denoise', settings: { strength: 20 } },
      { stepId: 'silence-trim', settings: { thresholdDb: -35 } },
    ],
  });
}

describe('r2m-voice-editor-page', () => {
  it('lists the catalog steps, plays the live WAV as Original, and gates Preview and Apply', async () => {
    const page = await render();

    expect(
      Array.from(page.querySelectorAll('[data-step]')).map((n) => n.getAttribute('data-step')),
    ).toEqual(['denoise', 'hiss-reduce', 'silence-trim']);
    expect(text(page.querySelector('.r2m-page-header__subtitle'))).toBe('Main');
    expect(page.querySelector('[data-testid="original-player"] audio')?.getAttribute('src')).toBe(
      '/workspace/dune/voices/alice/v1-main.wav?v=0',
    );
    expect(button(page, 'preview').disabled).toBe(true);
    expect(button(page, 'apply').disabled).toBe(true);
    expect(page.querySelector('[data-action="restore"]')).toBeNull();
    expect(
      page.querySelector<HTMLAnchorElement>('[data-action="back"]')?.getAttribute('href'),
    ).toBe('projects/dune/cast/alice');
  });

  it('renders the ticked steps in catalog order and stacks an After player per stage', async () => {
    api.on('POST', PREVIEW_URL, PREVIEW);
    const page = await render();
    await tick(page, 'silence-trim');
    await tick(page, 'denoise');
    expect(button(page, 'preview').disabled).toBe(false);

    await previewDefaultChain(page);

    const stages = Array.from(page.querySelectorAll('[data-stage]'));
    expect(stages.map((n) => n.getAttribute('data-stage'))).toEqual(['denoise', 'silence-trim']);
    expect(stages[1]!.querySelector('audio')?.getAttribute('src')).toContain(
      '/api/previews/p1/silence-trim.wav',
    );
    expect(text(page.querySelector('[data-testid="skipped-chip"]'))).toContain(
      'Skipped — trimmed result under 1000 ms',
    );
    expect(button(page, 'apply').disabled).toBe(false);
  });

  it('stales the render when a dial changes after Preview, until the next Preview', async () => {
    let previewId = 'p1';
    api.on('POST', PREVIEW_URL, () => ({ ...PREVIEW, previewId }));
    const page = await render();
    await tick(page, 'silence-trim');
    await tick(page, 'denoise');
    await previewDefaultChain(page);
    expect(button(page, 'apply').disabled).toBe(false);

    const strength = page.querySelector<HTMLInputElement>(
      '[data-key="strength"] input[type="number"]',
    )!;
    strength.value = '31';
    strength.dispatchEvent(new Event('input'));
    await settle();
    await page.rendered();

    expect(button(page, 'apply').disabled).toBe(true);
    expect(page.querySelector('[data-testid="stale-hint"]')).not.toBeNull();

    previewId = 'p2';
    button(page, 'preview').click();
    for (let i = 0; i < 3; i++) await settle();
    await page.rendered();
    const second = api.calls('POST', PREVIEW_URL)[1]!.body as { steps: unknown[] };
    expect(second.steps[0]).toEqual({ stepId: 'denoise', settings: { strength: 31 } });
    expect(button(page, 'apply').disabled).toBe(false);
    expect(page.querySelector('[data-testid="stale-hint"]')).toBeNull();
  });

  it('stales the render when a tick changes', async () => {
    api.on('POST', PREVIEW_URL, PREVIEW);
    const page = await render();
    await tick(page, 'silence-trim');
    await tick(page, 'denoise');
    await previewDefaultChain(page);

    await tick(page, 'hiss-reduce');

    expect(button(page, 'apply').disabled).toBe(true);
    expect(page.querySelector('[data-testid="hiss-hint"]')).not.toBeNull();
  });

  it('applies the heard previewId and switches Original to the stored original', async () => {
    api.on('POST', PREVIEW_URL, PREVIEW);
    api.on('POST', APPLY_URL, { ...VOICE, isEdited: true });
    const page = await render();
    await tick(page, 'silence-trim');
    await tick(page, 'denoise');
    await previewDefaultChain(page);

    button(page, 'apply').click();
    for (let i = 0; i < 3; i++) await settle();
    await page.rendered();

    expect(api.calls('POST', APPLY_URL)[0]?.body).toEqual({ previewId: 'p1' });
    expect(page.querySelector('[data-testid="edited-chip"]')).not.toBeNull();
    expect(page.querySelector('[data-action="restore"]')).not.toBeNull();
    expect(text(page.querySelector('[data-testid="action-note"]'))).toContain(
      'Voice audio updated.',
    );
    expect(page.querySelector('[data-testid="original-player"] audio')?.getAttribute('src')).toBe(
      '/api/projects/dune/voices/v1/original.wav?v=1',
    );
  });

  it('restores behind the destructive confirm and resets the chain', async () => {
    api.on('POST', RESTORE_URL, VOICE);
    const page = await render({ ...VOICE, isEdited: true });
    expect(page.querySelector('[data-testid="edited-chip"]')).not.toBeNull();
    await tick(page, 'denoise');

    confirmAnswer = false;
    button(page, 'restore').click();
    for (let i = 0; i < 3; i++) await settle();
    expect(confirms).toEqual(['Restore original audio?']);
    expect(api.calls('POST', RESTORE_URL)).toHaveLength(0);

    confirmAnswer = true;
    button(page, 'restore').click();
    for (let i = 0; i < 3; i++) await settle();
    await page.rendered();

    expect(api.calls('POST', RESTORE_URL)).toHaveLength(1);
    expect(page.querySelector('[data-testid="edited-chip"]')).toBeNull();
    expect(page.querySelector<HTMLInputElement>('[data-tick="denoise"]')!.checked).toBe(false);
    expect(text(page.querySelector('[data-testid="action-note"]'))).toContain(
      'Original audio restored.',
    );
  });

  it('shows the failure inline when a preview is refused', async () => {
    api.on('POST', PREVIEW_URL, problem(422, 'The voice has no audio.'));
    const page = await render();
    await tick(page, 'denoise');
    button(page, 'preview').click();
    for (let i = 0; i < 3; i++) await settle();
    await page.rendered();

    expect(text(page.querySelector('[data-testid="action-error"]'))).toContain(
      'The voice has no audio.',
    );
    expect(button(page, 'apply').disabled).toBe(true);
  });

  it('shows a message and a way back when the voice is gone', async () => {
    const page = await render(problem(404, 'no such voice'));

    expect(text(page.querySelector('.r2m-empty-state'))).toContain('That voice no longer exists.');
    expect(page.querySelector('[data-action="back-empty"]')?.getAttribute('href')).toBe(
      'projects/dune/cast',
    );
  });

  it('refuses a voice without audio', async () => {
    const page = await render({ ...VOICE, audioFileName: null });

    expect(text(page.querySelector('.r2m-empty-state'))).toContain(
      'This voice has no audio to edit.',
    );
  });
});
