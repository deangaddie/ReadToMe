import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { SettingsFieldDto, ProviderType, TextStepDto } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ConfirmService, type ConfirmOptions } from '@app/ui/dialogs';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { PROVIDER_PAGE_TAGS, type ProviderAreaKey } from './provider-area';
import type { ProviderConfig } from './provider-form';
import type { ProviderSettingsPage } from './provider-settings-page';
import './provider-settings-page';

/**
 * Ported from the Angular TestBed spec, case for case: the page of the area is created, appended,
 * and its HTTP goes through `FakeApi`; the hub is `FakeLive`; the discard prompt is a recorded
 * stub. The editor's managed-service resolve 404s harmlessly.
 */
const num = (key: string, label: string, extra: Partial<SettingsFieldDto> = {}) =>
  ({ key, label, kind: 'number', default: 1, nullable: false, ...extra }) as SettingsFieldDto;

/** A cut of what the host's schema endpoints answer per provider type. */
const SCHEMAS: Record<string, SettingsFieldDto[]> = {
  'paragraph-tts:VoxCpm2': [
    num('cfg_value', 'CFG Value', { min: 1, max: 5, step: 0.1, default: 2 }),
  ],
  'paragraph-tts:Chatterbox': [
    num('exaggeration', 'Exaggeration', { min: 0, max: 2, default: 0.5 }),
  ],
  'paragraph-tts:Qwen3Base': [num('temperature', 'Temperature', { min: 0, max: 2, default: 0.8 })],
  'paragraph-tts:Breeze': [
    num('top_k', 'Top K', { min: 1, step: 1, default: null, nullable: true }),
  ],
  'voice-design:VoxCpm2': [
    num('inference_timesteps', 'LocDiT Steps', { min: 1, max: 50, step: 1 }),
  ],
  'voice-design:Qwen3': [num('topK', 'Top K', { min: 1, step: 1, default: null, nullable: true })],
  'transcription:LocalWhisper': [],
  'semantic-similarity:MiniLmL6': [
    num('PassThreshold', 'Pass Threshold', { min: 0, max: 1, step: 0.01, default: 0.85 }),
  ],
  'semantic-similarity:MpnetBaseV2': [
    num('PassThreshold', 'Pass Threshold', { min: 0, max: 1, step: 0.01, default: 0.85 }),
  ],
};

const STEPS: TextStepDto[] = [
  {
    stepId: 'to-sentence-case',
    label: 'Sentence case',
    description: 'De-shouts all-caps text.',
    builtIn: true,
    options: [],
  },
];

function config(
  id: number,
  name: string,
  type: ProviderType,
  settingsJson: string,
): ProviderConfig {
  return { id, name, type, settingsJson };
}

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
    success: (m: string) => toasts.push(m),
    error: (m: string) => toasts.push(`error: ${m}`),
    warn: () => undefined,
    info: () => undefined,
    problem: (p: { detail?: string }) => toasts.push(`problem: ${p.detail}`),
  } as unknown as ToastService);
});
/** Angular's `http.verify()`: nothing was asked for that the case did not expect (the managed-service resolve may 404). */
afterEach(() => {
  document.body.replaceChildren();
  expect(api.unrouted.filter((r) => r.path !== '/api/ai-services/resolve')).toEqual([]);
});

function routeLoad(area: ProviderAreaKey, configs: ProviderConfig[], active = configs[0]) {
  api.on('GET', `/api/settings/${area}`, configs);
  api.on('GET', `/api/settings/${area}/active`, active ?? problem(404, 'none'));
}

/** The schema route answers per `?type=`, so a type change finds its schema without re-routing. */
function routeSchemas(area: ProviderAreaKey) {
  api.on('GET', `/api/settings/${area}/schema`, (_body: unknown, url: URL) => {
    const type = url.searchParams.get('type');
    const fields = SCHEMAS[`${area}:${type}`];
    return fields ? { type, fields } : problem(500, 'no schema');
  });
}

async function flush(page: ProviderSettingsPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

/** Renders `area` with one saved config of `type`, open in the editor. */
async function render(area: ProviderAreaKey, type: ProviderType, settings: object = {}) {
  const saved = config(1, 'main', type, JSON.stringify({ baseUrl: 'http://svc:1', ...settings }));
  if (area === 'voice-design')
    api.on('GET', '/api/settings/voice-design/sample-text', {
      text: null,
      default: 'The default sentence.',
      maxLength: 40,
    });
  routeLoad(area, [saved]);
  routeSchemas(area);
  if (area === 'paragraph-tts') api.on('GET', '/api/settings/paragraph-tts/1/text-steps', STEPS);
  const page = document.createElement(PROVIDER_PAGE_TAGS[area]) as ProviderSettingsPage;
  document.body.append(page);
  await flush(page);
  return { page, saved };
}

async function type(page: ProviderSettingsPage, selector: string, value: string) {
  const input = page.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flush(page);
}

/** A checkbox or switch: the browser toggles it on click; here the state is set and `change` fired. */
async function check(page: ProviderSettingsPage, selector: string, checked = true) {
  const box = page.querySelector<HTMLInputElement>(selector)!;
  box.checked = checked;
  box.dispatchEvent(new Event('change'));
  await flush(page);
}

async function setType(page: ProviderSettingsPage, value: ProviderType) {
  const select = page.querySelector<HTMLSelectElement>('select[data-field="type"]')!;
  select.value = value;
  select.dispatchEvent(new Event('change'));
  await flush(page);
}

const button = (el: Element, label: string) =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

const fieldKeys = (el: Element) =>
  Array.from(el.querySelectorAll('.r2m-settings-form__field')).map((f) =>
    f.getAttribute('data-key'),
  );

const error = (el: Element) => el.querySelector('.provider-editor__error')?.textContent?.trim();

describe('r2m-provider-settings-page', () => {
  describe('schema → form', () => {
    const cases: [ProviderAreaKey, ProviderType, string[]][] = [
      ['paragraph-tts', 'VoxCpm2', ['maxChunkChars', 'carrierPrefixEnabled', 'cfg_value']],
      ['paragraph-tts', 'Chatterbox', ['maxChunkChars', 'carrierPrefixEnabled', 'exaggeration']],
      ['paragraph-tts', 'Qwen3Base', ['maxChunkChars', 'carrierPrefixEnabled', 'temperature']],
      ['paragraph-tts', 'Breeze', ['maxChunkChars', 'carrierPrefixEnabled', 'top_k']],
      ['voice-design', 'VoxCpm2', ['inference_timesteps']],
      ['voice-design', 'Qwen3', ['topK']],
      ['transcription', 'LocalWhisper', []],
      ['semantic-similarity', 'MiniLmL6', ['PassThreshold']],
      ['semantic-similarity', 'MpnetBaseV2', ['PassThreshold']],
    ];

    for (const [area, providerType, keys] of cases) {
      it(`${area} type ${providerType} edits ${keys.join(', ') || 'only its base URL'}`, async () => {
        const { page } = await render(area, providerType);

        expect(fieldKeys(page)).toEqual(keys);
        expect(page.querySelector<HTMLInputElement>('[data-field="baseUrl"]')?.value).toBe(
          'http://svc:1',
        );
      });
    }

    it('shows the stored value over the schema default, whatever case its key is stored in', async () => {
      const { page } = await render('semantic-similarity', 'MiniLmL6', { PassThreshold: 0.6 });

      const box = page.querySelector<HTMLInputElement>(
        '[data-key="PassThreshold"] input[type=number]',
      );
      expect(box?.value).toBe('0.6');
      expect(page.querySelector('.r2m-config-list__row')?.textContent).toContain('threshold 0.60');
    });

    it('asks for the new type’s schema and starts its tuning from that type’s defaults', async () => {
      const { page } = await render('voice-design', 'VoxCpm2', { inference_timesteps: 30 });

      await setType(page, 'Qwen3');

      expect(fieldKeys(page)).toEqual(['topK']);
      expect(page.querySelector<HTMLInputElement>('[data-key="topK"] input')?.value).toBe('');
      expect(page.hasUnsavedChanges()).toBe(true);
    });
  });

  describe('type change', () => {
    it('keeps what the area holds per config and drops the old type’s tuning', async () => {
      const { page } = await render('paragraph-tts', 'VoxCpm2', {
        maxChunkChars: 350,
        cfg_value: 4,
      });

      await setType(page, 'Chatterbox');

      expect(page.querySelector<HTMLInputElement>('[data-key="maxChunkChars"] input')?.value).toBe(
        '350',
      );
      api.on('PUT', '/api/settings/paragraph-tts/1', (body: unknown) => body);
      button(page, 'Save').click();
      await flush(page);
      const put = api.calls('PUT', '/api/settings/paragraph-tts/1')[0]!;
      expect(JSON.parse((put.body as ProviderConfig).settingsJson)).toEqual({
        baseUrl: 'http://svc:1',
        maxChunkChars: 350,
        carrierPrefixEnabled: false,
        exaggeration: 0.5,
      });
    });

    it('a schema that fails does not leave its message on a type that loaded', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      api.on('GET', '/api/settings/voice-design/schema', (_body: unknown, url: URL) =>
        url.searchParams.get('type') === 'Qwen3'
          ? problem(500, 'no schema')
          : { type: 'VoxCpm2', fields: SCHEMAS['voice-design:VoxCpm2'] },
      );

      await setType(page, 'Qwen3');
      expect(error(page)).toContain('could not be loaded');

      await setType(page, 'VoxCpm2');
      expect(error(page)).toBeUndefined();
    });
  });

  describe('TTS', () => {
    it('offers the carrier length only while the carrier prefix is on', async () => {
      const { page } = await render('paragraph-tts', 'VoxCpm2');
      expect(fieldKeys(page)).not.toContain('carrierMaxTargetChars');

      await check(page, '[data-key="carrierPrefixEnabled"] input[role=switch]');

      expect(fieldKeys(page)).toContain('carrierMaxTargetChars');
    });

    it('saves text-processing options and edited substitution rows', async () => {
      const { page, saved } = await render('paragraph-tts', 'VoxCpm2');

      await check(page, '[data-step="to-sentence-case"] input');
      page.querySelector<HTMLButtonElement>('[data-action="add-substitution"]')!.click();
      await flush(page);
      await type(page, '[data-role="substitution"] [data-field="fromText"]', 'Dr.');
      await type(page, '[data-role="substitution"] [data-field="toText"]', 'Doctor');
      await type(page, '[data-option="wordMinLength"]', '8');
      let stored: ProviderConfig | null = null;
      api.on('PUT', '/api/settings/paragraph-tts/1', (body: unknown) => {
        stored = { ...saved, ...(body as ProviderConfig) };
        routeLoad('paragraph-tts', [stored]);
        return stored;
      });
      button(page, 'Save').click();
      await flush(page);

      const body = api.calls('PUT', '/api/settings/paragraph-tts/1')[0]!.body as ProviderConfig;
      const [row] = body.substitutionSteps!;
      expect(row).toMatchObject({ fromText: 'Dr.', toText: 'Doctor', order: 0 });
      expect(body.enabledStepIds).toEqual(['to-sentence-case', row!.id]);
      expect(body.toSentenceCaseConfig).toMatchObject({
        paragraphEnabled: true,
        wordEnabled: true,
        wordMinLength: 8,
      });
      expect(JSON.parse(body.settingsJson)).toEqual({
        baseUrl: 'http://svc:1',
        maxChunkChars: 500,
        carrierPrefixEnabled: false,
        cfg_value: 2,
      });

      expect(page.hasUnsavedChanges()).toBe(false);
      expect(page.querySelector<HTMLInputElement>('[data-field="toText"]')?.value).toBe('Doctor');
    });

    it('removing a substitution row drops it from the draft', async () => {
      const { page } = await render('paragraph-tts', 'VoxCpm2');
      page.querySelector<HTMLButtonElement>('[data-action="add-substitution"]')!.click();
      await flush(page);
      expect(page.querySelectorAll('[data-role="substitution"]').length).toBe(1);

      page.querySelector<HTMLButtonElement>('[data-action="remove-substitution"]')!.click();
      await flush(page);

      expect(page.querySelectorAll('[data-role="substitution"]').length).toBe(0);
      expect(page.hasUnsavedChanges()).toBe(false);
    });

    it('refuses a chunk size below 1', async () => {
      const { page } = await render('paragraph-tts', 'VoxCpm2');

      await type(page, '[data-key="maxChunkChars"] input', '0');
      button(page, 'Save').click();
      await flush(page);

      expect(error(page)).toBe('Max chunk chars must be 1 or more.');
    });
  });

  describe('config list', () => {
    const SIM = '/api/settings/semantic-similarity';
    const other = config(
      2,
      'other',
      'MiniLmL6',
      JSON.stringify({ BaseUrl: 'http://svc:2', PassThreshold: 0.8 }),
    );

    /** Similarity with two saved configs, `main` active and open. */
    async function renderTwo() {
      const main = config(
        1,
        'main',
        'MiniLmL6',
        JSON.stringify({ BaseUrl: 'http://svc:1', PassThreshold: 0.85 }),
      );
      routeLoad('semantic-similarity', [main, other], main);
      routeSchemas('semantic-similarity');
      const page = document.createElement(
        PROVIDER_PAGE_TAGS['semantic-similarity'],
      ) as ProviderSettingsPage;
      document.body.append(page);
      await flush(page);
      return { page, main };
    }

    const rowAction = (page: Element, name: string, action: string) =>
      Array.from(
        page.querySelectorAll<HTMLButtonElement>(
          `[role="menu"][aria-label="Actions for ${name}"] [role="menuitem"]`,
        ),
      ).find((b) => b.textContent?.trim().endsWith(action))!;

    const names = (page: Element) =>
      Array.from(page.querySelectorAll('.r2m-config-list__name'), (n) => n.textContent);

    it('New → Save POSTs the built config and selects it', async () => {
      const { page, main } = await renderTwo();
      page.querySelector<HTMLButtonElement>('.r2m-config-list__head .r2m-button--filled')!.click();
      await flush(page);
      expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe(
        'New configuration',
      );

      await type(page, '[data-field="name"]', 'fresh');
      await type(page, '[data-field="baseUrl"]', 'http://fresh:8200');
      await type(page, '[data-key="PassThreshold"] input[type=number]', '0.7');
      const fresh = config(
        3,
        'fresh',
        'MiniLmL6',
        '{"BaseUrl":"http://fresh:8200","PassThreshold":0.7}',
      );
      api.on('POST', SIM, fresh);
      routeLoad('semantic-similarity', [main, other, fresh], main);
      button(page, 'Save').click();
      await flush(page);

      const body = api.calls('POST', SIM)[0]!.body as ProviderConfig;
      expect(body).toMatchObject({ id: 0, name: 'fresh', type: 'MiniLmL6' });
      expect(JSON.parse(body.settingsJson)).toEqual({
        baseUrl: 'http://fresh:8200',
        PassThreshold: 0.7,
      });
      expect(page.querySelector('.r2m-config-editor-frame__title')?.textContent).toBe('fresh');
      const selected = page.querySelector('.r2m-config-list__row--selected');
      expect(selected?.querySelector('.r2m-config-list__name')?.textContent).toBe('fresh');
      expect(selected?.textContent).toContain('threshold 0.70');
      expect(toasts).toEqual(['Configuration created']);
    });

    it('Make active PUTs /active and moves the Active marker', async () => {
      const { page, main } = await renderTwo();
      api.on('PUT', `${SIM}/active`, undefined);
      routeLoad('semantic-similarity', [main, other], other);

      rowAction(page, 'other', 'Make active').click();
      await flush(page);

      expect(api.calls('PUT', `${SIM}/active`).map((c) => c.body)).toEqual([{ id: 2 }]);
      const rows = Array.from(page.querySelectorAll('.r2m-config-list__row'));
      expect(rows[0]!.textContent).not.toContain('Active');
      expect(rows[1]!.textContent).toContain('Active');
    });

    it('Delete asks a destructive confirm, then DELETEs and drops the row', async () => {
      const { page, main } = await renderTwo();
      api.on('DELETE', `${SIM}/2`, undefined);

      confirmAnswer = false;
      rowAction(page, 'other', 'Delete').click();
      await flush(page);
      expect(confirms[0]).toMatchObject({ title: 'Delete other?', destructive: true });
      expect(api.calls('DELETE', `${SIM}/2`)).toHaveLength(0);

      confirmAnswer = true;
      routeLoad('semantic-similarity', [main], main);
      rowAction(page, 'other', 'Delete').click();
      await flush(page);

      expect(api.calls('DELETE', `${SIM}/2`)).toHaveLength(1);
      expect(names(page)).toEqual(['main']);
      expect(toasts).toEqual(['Deleted other']);
    });
  });

  describe('stored settings', () => {
    it('renaming a stored config PUTs every key it holds, edited or not', async () => {
      const stored = {
        BaseUrl: 'http://example-qwen:8004',
        ModelId: 'qwen3-design-bf16',
        Language: 'en',
        Temperature: null,
        TopP: null,
        TopK: 40,
        RepetitionPenalty: null,
        MaxNewTokens: null,
        Seed: null,
      };
      api.on('GET', '/api/settings/voice-design/sample-text', {
        text: null,
        default: 'The default sentence.',
        maxLength: 40,
      });
      routeLoad('voice-design', [config(1, 'stored', 'Qwen3', JSON.stringify(stored))]);
      routeSchemas('voice-design');
      const page = document.createElement(
        PROVIDER_PAGE_TAGS['voice-design'],
      ) as ProviderSettingsPage;
      document.body.append(page);
      await flush(page);
      expect(page.querySelector<HTMLInputElement>('[data-key="topK"] input')?.value).toBe('40');

      await type(page, '[data-field="name"]', 'renamed');
      api.on('PUT', '/api/settings/voice-design/1', (body: unknown) => body);
      // The sample-text card has a Save of its own; this is the editor's.
      button(page.querySelector('r2m-provider-config-editor')!, 'Save').click();
      await flush(page);

      const body = api.calls('PUT', '/api/settings/voice-design/1')[0]!.body as ProviderConfig;
      expect(body).toMatchObject({ id: 1, name: 'renamed', type: 'Qwen3' });
      // The host rewrites key case; every stored key and value must reach it.
      const lowered = (o: object) =>
        Object.fromEntries(Object.entries(o).map(([k, v]) => [k.toLowerCase(), v]));
      expect(lowered(JSON.parse(body.settingsJson))).toEqual(lowered(stored));
    });
  });

  describe('validation', () => {
    it('every page asks for a name and an absolute base URL before it sends anything', async () => {
      const { page } = await render('transcription', 'LocalWhisper');

      await type(page, '[data-field="name"]', ' ');
      button(page, 'Save').click();
      await flush(page);
      expect(error(page)).toBe('Name is required.');

      await type(page, '[data-field="name"]', 'whisper');
      await type(page, '[data-field="baseUrl"]', 'localhost');
      button(page, 'Save').click();
      await flush(page);
      expect(error(page)).toBe(
        'Base URL must be a valid absolute URL (e.g. http://localhost:9000).',
      );
      expect(api.calls('PUT', '/api/settings/transcription/1').length).toBe(0);
    });

    it('similarity wants its threshold strictly between 0 and 1', async () => {
      const { page } = await render('semantic-similarity', 'MiniLmL6');

      await type(page, '[data-key="PassThreshold"] input[type=number]', '1');
      button(page, 'Save').click();
      await flush(page);
      expect(error(page)).toBe('Pass threshold must be between 0 and 1 (exclusive).');

      await type(page, '[data-key="PassThreshold"] input[type=number]', '1.5');
      button(page, 'Save').click();
      await flush(page);
      expect(error(page)).toBe('Pass Threshold must be between 0 and 1.');
    });
  });

  describe('test actions', () => {
    it('similarity compares two texts and reports the verdict', async () => {
      const { page } = await render('semantic-similarity', 'MiniLmL6');
      api.on('POST', '/api/settings/semantic-similarity/1/test', {
        score: 0.9123,
        threshold: 0.85,
        pass: true,
      });

      await type(page, '[data-field="text1"]', 'The cat sat.');
      await type(page, '[data-field="text2"]', 'A cat was sitting.');
      page.querySelector<HTMLButtonElement>('r2m-similarity-test [data-action="test"]')!.click();
      await flush(page);

      expect(api.calls('POST', '/api/settings/semantic-similarity/1/test')[0]?.body).toEqual({
        text1: 'The cat sat.',
        text2: 'A cat was sitting.',
      });
      expect(page.querySelector('[data-role="score"]')?.textContent).toContain(
        'Score 0.912 — PASS (threshold 0.85)',
      );
    });

    it('reports a provider that is down with its reason', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      api.on('POST', '/api/settings/voice-design/1/test', problem(422, 'Connection refused'));

      await type(page, '[data-field="prompt"]', 'A warm old man');
      page.querySelector<HTMLButtonElement>('r2m-voice-design-test [data-action="test"]')!.click();
      await flush(page);

      expect(page.querySelector('r2m-voice-design-test')?.textContent).toContain(
        'Test failed: Connection refused',
      );
    });

    it('voice design plays the designed audio', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      api.on('POST', '/api/settings/voice-design/1/test', {
        audioBase64: 'UklGRg==',
        contentType: 'audio/wav',
      });

      await type(page, '[data-field="prompt"]', 'A warm old man');
      page.querySelector<HTMLButtonElement>('r2m-voice-design-test [data-action="test"]')!.click();
      await flush(page);

      expect(page.querySelector('r2m-voice-design-test r2m-audio-player')).not.toBeNull();
    });

    it('transcription POSTs a dropped file as FormData and shows the transcript', async () => {
      const { page } = await render('transcription', 'LocalWhisper');
      api.on('POST', '/api/settings/transcription/1/test', { transcript: 'Hello there.' });
      const clip = new File([new Uint8Array([1, 2, 3, 4])], 'clip.wav', { type: 'audio/wav' });

      page
        .querySelector('r2m-transcription-test r2m-file-drop')!
        .dispatchEvent(new CustomEvent('files', { detail: [clip] }));
      await flush(page);

      const form = api.calls('POST', '/api/settings/transcription/1/test')[0]?.body as FormData;
      expect(form).toBeInstanceOf(FormData);
      expect((form.get('file') as File).name).toBe('clip.wav');
      const transcript = page.querySelector('r2m-transcription-test [data-role="transcript"]');
      expect(transcript?.textContent).toContain('Transcript');
      expect(transcript?.textContent).toContain('Hello there.');
    });

    it('TTS has no test action', async () => {
      const { page } = await render('paragraph-tts', 'VoxCpm2');

      expect(page.querySelector('[data-action="test"]')).toBeNull();
    });
  });

  describe('voice-design sample text', () => {
    it('Save is dirty-gated and Reset to default refills the draft', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      const save = page.querySelector<HTMLButtonElement>('[data-action="save-sample-text"]')!;
      expect(save.disabled).toBe(true);

      await type(page, '[data-field="sampleText"]', 'My own sentence.');
      expect(save.disabled).toBe(false);
      expect(page.hasUnsavedChanges()).toBe(true);
      api.on('PUT', '/api/settings/voice-design/sample-text', {
        text: 'My own sentence.',
        default: 'The default sentence.',
        maxLength: 40,
      });
      save.click();
      await flush(page);
      expect(api.calls('PUT', '/api/settings/voice-design/sample-text')[0]?.body).toEqual({
        text: 'My own sentence.',
      });
      expect(save.disabled).toBe(true);

      page.querySelector<HTMLButtonElement>('[data-action="reset-sample-text"]')!.click();
      await flush(page);
      expect(page.querySelector<HTMLTextAreaElement>('[data-field="sampleText"]')?.value).toBe(
        'The default sentence.',
      );
      expect(save.disabled).toBe(false);
    });

    it('is busy while a save is in flight and settles when it lands', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      const card = page.querySelector('r2m-voice-design-sample-text')!;
      expect(card.hasAttribute('aria-busy')).toBe(false);

      await type(page, '[data-field="sampleText"]', 'My own sentence.');
      let land!: () => void;
      api.on(
        'PUT',
        '/api/settings/voice-design/sample-text',
        () =>
          new Promise((resolve) => {
            land = () =>
              resolve({
                text: 'My own sentence.',
                default: 'The default sentence.',
                maxLength: 40,
              });
          }),
      );
      page.querySelector<HTMLButtonElement>('[data-action="save-sample-text"]')!.click();
      await flush(page);
      expect(card.getAttribute('aria-busy')).toBe('true');

      land();
      await flush(page);
      expect(card.hasAttribute('aria-busy')).toBe(false);
    });

    it('counts the draft against the host’s cap and will not save past it', async () => {
      const { page } = await render('voice-design', 'VoxCpm2');
      const save = page.querySelector<HTMLButtonElement>('[data-action="save-sample-text"]')!;
      const count = () =>
        page.querySelector('[data-testid="sample-text-count"]')?.textContent?.trim();
      expect(count()).toBe('21 / 40');

      await type(page, '[data-field="sampleText"]', 'x'.repeat(40));
      expect(count()).toBe('40 / 40');
      expect(save.disabled).toBe(false);

      await type(page, '[data-field="sampleText"]', 'x'.repeat(41));
      expect(count()).toBe('41 / 40');
      expect(save.disabled).toBe(true);
    });
  });

  describe('live', () => {
    it('reloads on settingsChanged for its area only and follows a config edited elsewhere', async () => {
      const { page, saved } = await render('transcription', 'LocalWhisper');
      const elsewhere = { ...saved, settingsJson: JSON.stringify({ baseUrl: 'http://svc:2' }) };
      routeLoad('transcription', [elsewhere]);

      live.emit('settingsChanged', { area: 'paragraph-tts' });
      await flush(page);
      expect(api.calls('GET', '/api/settings/transcription').length).toBe(1);

      live.emit('settingsChanged', { area: 'transcription' });
      await flush(page);
      expect(api.calls('GET', '/api/settings/transcription').length).toBe(2);
      expect(page.querySelector<HTMLInputElement>('[data-field="baseUrl"]')?.value).toBe(
        'http://svc:2',
      );
      expect(page.querySelector('.r2m-config-editor-frame__dirty')).toBeNull();
    });
  });
});
