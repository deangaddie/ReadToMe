import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { ProviderSettingsField, TextStep } from '@app/api';
import { ProviderAreaKey } from './provider-area';
import { ProviderConfig } from './provider-form';
import { PROVIDER_AREA_DATA, ProviderSettingsPage } from './provider-settings-page';

const num = (key: string, label: string, extra: Partial<ProviderSettingsField> = {}) =>
  ({ key, label, kind: 'number', default: 1, ...extra }) as ProviderSettingsField;

/** A cut of what the host's schema endpoints answer per provider type. */
const SCHEMAS: Record<string, ProviderSettingsField[]> = {
  'paragraph-tts:0': [num('cfg_value', 'CFG Value', { min: 1, max: 5, step: 0.1, default: 2 })],
  'paragraph-tts:1': [num('exaggeration', 'Exaggeration', { min: 0, max: 2, default: 0.5 })],
  'paragraph-tts:2': [num('temperature', 'Temperature', { min: 0, max: 2, default: 0.8 })],
  'paragraph-tts:3': [num('top_k', 'Top K', { min: 1, step: 1, default: null, nullable: true })],
  'voice-design:0': [num('inference_timesteps', 'LocDiT Steps', { min: 1, max: 50, step: 1 })],
  'voice-design:1': [num('topK', 'Top K', { min: 1, step: 1, default: null, nullable: true })],
  'transcription:0': [],
  'semantic-similarity:0': [
    num('PassThreshold', 'Pass Threshold', { min: 0, max: 1, step: 0.01, default: 0.85 }),
  ],
  'semantic-similarity:1': [
    num('PassThreshold', 'Pass Threshold', { min: 0, max: 1, step: 0.01, default: 0.85 }),
  ],
};

const STEPS: TextStep[] = [
  {
    stepId: 'to-sentence-case',
    label: 'Sentence case',
    description: 'De-shouts all-caps text.',
    builtIn: true,
    options: [],
  },
];

function config(id: number, name: string, type: number, settingsJson: string): ProviderConfig {
  return { id, name, type, settingsJson };
}

describe('ProviderSettingsPage', () => {
  let http: HttpTestingController;

  async function configure(area: ProviderAreaKey) {
    await TestBed.configureTestingModule({
      imports: [ProviderSettingsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { data: { [PROVIDER_AREA_DATA]: area } } },
        },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

  async function eventually(method: string, url: string): Promise<TestRequest> {
    for (let i = 0; i < 100; i++) {
      const [match] = http.match((r) => r.method === method && r.urlWithParams === url);
      if (match) return match;
      await settle(10);
    }
    throw new Error(`No ${method} ${url}`);
  }

  async function stable(fixture: ComponentFixture<unknown>) {
    await settle();
    await fixture.whenStable();
  }

  async function flushLoad(area: ProviderAreaKey, configs: ProviderConfig[], active = configs[0]) {
    (await eventually('GET', `/api/settings/${area}`)).flush(configs);
    const activeReq = await eventually('GET', `/api/settings/${area}/active`);
    if (active) activeReq.flush(active);
    else activeReq.flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
  }

  async function flushSchema(area: ProviderAreaKey, type: number) {
    (await eventually('GET', `/api/settings/${area}/schema?type=${type}`)).flush({
      type: String(type),
      fields: SCHEMAS[`${area}:${type}`],
    });
  }

  /** None of the test URLs is a managed container. */
  async function flushResolve(baseUrl: string) {
    (await eventually('GET', `/api/ai-services/resolve?baseUrl=${baseUrl}`)).flush(
      { title: 'Not Found' },
      { status: 404, statusText: 'Not Found' },
    );
  }

  /** Renders `area` with one saved config of `type`, open in the editor. */
  async function render(area: ProviderAreaKey, type: number, settings: object = {}) {
    await configure(area);
    const saved = config(1, 'main', type, JSON.stringify({ baseUrl: 'http://svc:1', ...settings }));
    const fixture = TestBed.createComponent(ProviderSettingsPage);
    if (area === 'voice-design')
      (await eventually('GET', '/api/settings/voice-design/sample-text')).flush({
        text: null,
        default: 'The default sentence.',
      });
    await flushLoad(area, [saved]);
    await flushSchema(area, type);
    if (area === 'paragraph-tts')
      (await eventually('GET', '/api/settings/paragraph-tts/1/text-steps')).flush(STEPS);
    await flushResolve('http://svc:1');
    await stable(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement, saved };
  }

  function type(el: HTMLElement, selector: string, value: string) {
    const input = el.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  const button = (el: HTMLElement, label: string) =>
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;

  const fieldKeys = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('.r2m-settings-form__field')).map((f) =>
      f.getAttribute('data-key'),
    );

  const error = (el: HTMLElement) => el.querySelector('.provider-editor__error')?.textContent;

  describe('schema → form', () => {
    const cases: [ProviderAreaKey, number, string[]][] = [
      ['paragraph-tts', 0, ['maxChunkChars', 'carrierPrefixEnabled', 'cfg_value']],
      ['paragraph-tts', 1, ['maxChunkChars', 'carrierPrefixEnabled', 'exaggeration']],
      ['paragraph-tts', 2, ['maxChunkChars', 'carrierPrefixEnabled', 'temperature']],
      ['paragraph-tts', 3, ['maxChunkChars', 'carrierPrefixEnabled', 'top_k']],
      ['voice-design', 0, ['inference_timesteps']],
      ['voice-design', 1, ['apiKey', 'model', 'topK']],
      ['transcription', 0, []],
      ['semantic-similarity', 0, ['PassThreshold']],
      ['semantic-similarity', 1, ['PassThreshold']],
    ];

    for (const [area, providerType, keys] of cases) {
      it(`${area} type ${providerType} edits ${keys.join(', ') || 'only its base URL'}`, async () => {
        const { el } = await render(area, providerType);

        expect(fieldKeys(el)).toEqual(keys);
        expect(el.querySelector<HTMLInputElement>('[data-field="baseUrl"]')?.value).toBe(
          'http://svc:1',
        );
      });
    }

    it('shows the stored value over the schema default, whatever case its key is stored in', async () => {
      const { el } = await render('semantic-similarity', 0, { PassThreshold: 0.6 });

      const box = el.querySelector<HTMLInputElement>('[data-key="PassThreshold"] input[type=number]');
      expect(box?.value).toBe('0.6');
      expect(el.querySelector('.r2m-config-list__row')?.textContent).toContain('threshold 0.60');
    });

    it('asks for the new type’s schema and starts its tuning from that type’s defaults', async () => {
      const { fixture, el } = await render('voice-design', 0, { inference_timesteps: 30 });

      fixture.componentInstance['editor']()!['setType'](1);
      await flushSchema('voice-design', 1);
      await stable(fixture);

      expect(fieldKeys(el)).toEqual(['apiKey', 'model', 'topK']);
      expect(el.querySelector<HTMLInputElement>('[data-key="apiKey"] input')?.type).toBe('password');
      expect(fixture.componentInstance.hasUnsavedChanges()).toBe(true);
    });
  });

  describe('type change', () => {
    it('keeps what the area holds per config and drops the old type’s tuning', async () => {
      const { fixture, el } = await render('paragraph-tts', 0, { maxChunkChars: 350, cfg_value: 4 });

      fixture.componentInstance['editor']()!['setType'](1);
      await flushSchema('paragraph-tts', 1);
      await stable(fixture);

      expect(el.querySelector<HTMLInputElement>('[data-key="maxChunkChars"] input')?.value).toBe(
        '350',
      );
      button(el, 'Save').click();
      const put = await eventually('PUT', '/api/settings/paragraph-tts/1');
      expect(JSON.parse((put.request.body as ProviderConfig).settingsJson)).toEqual({
        baseUrl: 'http://svc:1',
        maxChunkChars: 350,
        carrierPrefixEnabled: false,
        exaggeration: 0.5,
      });
      put.flush(put.request.body);
      await flushLoad('paragraph-tts', [put.request.body as ProviderConfig]);
      (await eventually('GET', '/api/settings/paragraph-tts/1/text-steps')).flush(STEPS);
      await stable(fixture);
    });

    it('a schema that fails does not leave its message on a type that loaded', async () => {
      const { fixture, el } = await render('voice-design', 0);

      fixture.componentInstance['editor']()!['setType'](1);
      (await eventually('GET', '/api/settings/voice-design/schema?type=1')).flush(
        { title: 'Boom', detail: 'no schema' },
        { status: 500, statusText: 'Server Error' },
      );
      await stable(fixture);
      expect(error(el)).toContain('could not be loaded');

      fixture.componentInstance['editor']()!['setType'](0);
      await stable(fixture);
      expect(error(el)).toBeUndefined();
    });
  });

  describe('TTS', () => {
    it('offers the carrier length only while the carrier prefix is on', async () => {
      const { fixture, el } = await render('paragraph-tts', 0);
      expect(fieldKeys(el)).not.toContain('carrierMaxTargetChars');

      el.querySelector<HTMLButtonElement>('[data-key="carrierPrefixEnabled"] button')!.click();
      await stable(fixture);

      expect(fieldKeys(el)).toContain('carrierMaxTargetChars');
    });

    it('saves text-processing options and edited substitution rows', async () => {
      const { fixture, el, saved } = await render('paragraph-tts', 0);

      el.querySelector<HTMLInputElement>('[data-step="to-sentence-case"] input')!.click();
      await stable(fixture);
      el.querySelector<HTMLButtonElement>('[data-action="add-substitution"]')!.click();
      await stable(fixture);
      type(el, '[data-role="substitution"] [data-field="fromText"]', 'Dr.');
      type(el, '[data-role="substitution"] [data-field="toText"]', 'Doctor');
      type(el, '[data-option="wordMinLength"]', '8');
      await stable(fixture);
      button(el, 'Save').click();

      const put = await eventually('PUT', '/api/settings/paragraph-tts/1');
      const body = put.request.body as ProviderConfig;
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

      const stored = { ...saved, ...body };
      put.flush(stored);
      await flushLoad('paragraph-tts', [stored]);
      (await eventually('GET', '/api/settings/paragraph-tts/1/text-steps')).flush(STEPS);
      await stable(fixture);
      expect(fixture.componentInstance.hasUnsavedChanges()).toBe(false);
      expect(el.querySelector<HTMLInputElement>('[data-field="toText"]')?.value).toBe('Doctor');
    });

    it('removing a substitution row drops it from the draft', async () => {
      const { fixture, el } = await render('paragraph-tts', 0);
      el.querySelector<HTMLButtonElement>('[data-action="add-substitution"]')!.click();
      await stable(fixture);
      expect(el.querySelectorAll('[data-role="substitution"]').length).toBe(1);

      el.querySelector<HTMLButtonElement>('[data-action="remove-substitution"]')!.click();
      await stable(fixture);

      expect(el.querySelectorAll('[data-role="substitution"]').length).toBe(0);
      expect(fixture.componentInstance.hasUnsavedChanges()).toBe(false);
    });

    it('refuses a chunk size below 1', async () => {
      const { fixture, el } = await render('paragraph-tts', 0);

      type(el, '[data-key="maxChunkChars"] input', '0');
      await stable(fixture);
      button(el, 'Save').click();
      await stable(fixture);

      expect(error(el)).toBe('Max chunk chars must be 1 or more.');
    });
  });

  describe('validation', () => {
    it('every page asks for a name and an absolute base URL before it sends anything', async () => {
      const { fixture, el } = await render('transcription', 0);

      type(el, '[data-field="name"]', ' ');
      await stable(fixture);
      button(el, 'Save').click();
      await stable(fixture);
      expect(error(el)).toBe('Name is required.');

      type(el, '[data-field="name"]', 'whisper');
      type(el, '[data-field="baseUrl"]', 'localhost');
      await flushResolveNever();
      button(el, 'Save').click();
      await stable(fixture);
      expect(error(el)).toBe('Base URL must be a valid absolute URL (e.g. http://localhost:9000).');
    });

    /** A URL that is not absolute is never resolved; just let the debounce pass. */
    const flushResolveNever = () => settle(450);

    it('similarity wants its threshold strictly between 0 and 1', async () => {
      const { fixture, el } = await render('semantic-similarity', 0);

      type(el, '[data-key="PassThreshold"] input[type=number]', '1');
      await stable(fixture);
      button(el, 'Save').click();
      await stable(fixture);
      expect(error(el)).toBe('Pass threshold must be between 0 and 1 (exclusive).');

      type(el, '[data-key="PassThreshold"] input[type=number]', '1.5');
      await stable(fixture);
      button(el, 'Save').click();
      await stable(fixture);
      expect(error(el)).toBe('Pass Threshold must be between 0 and 1.');
    });
  });

  describe('test actions', () => {
    it('similarity compares two texts and reports the verdict', async () => {
      const { fixture, el } = await render('semantic-similarity', 0);

      type(el, '[data-field="text1"]', 'The cat sat.');
      type(el, '[data-field="text2"]', 'A cat was sitting.');
      await stable(fixture);
      el.querySelector<HTMLButtonElement>('app-similarity-test [data-action="test"]')!.click();
      const post = await eventually('POST', '/api/settings/semantic-similarity/1/test');
      expect(post.request.body).toEqual({ text1: 'The cat sat.', text2: 'A cat was sitting.' });
      post.flush({ score: 0.9123, threshold: 0.85, pass: true });
      await stable(fixture);

      expect(el.querySelector('[data-role="score"]')?.textContent).toContain(
        'Score 0.912 — PASS (threshold 0.85)',
      );
    });

    it('reports a provider that is down with its reason', async () => {
      const { fixture, el } = await render('voice-design', 0);

      type(el, '[data-field="prompt"]', 'A warm old man');
      await stable(fixture);
      el.querySelector<HTMLButtonElement>('app-voice-design-test [data-action="test"]')!.click();
      (await eventually('POST', '/api/settings/voice-design/1/test')).flush(
        { title: 'Unprocessable', detail: 'Connection refused' },
        { status: 422, statusText: 'Unprocessable Entity' },
      );
      await stable(fixture);

      expect(el.querySelector('app-voice-design-test')?.textContent).toContain(
        'Test failed: Connection refused',
      );
    });

    it('voice design plays the designed audio', async () => {
      const { fixture, el } = await render('voice-design', 0);

      type(el, '[data-field="prompt"]', 'A warm old man');
      await stable(fixture);
      el.querySelector<HTMLButtonElement>('app-voice-design-test [data-action="test"]')!.click();
      (await eventually('POST', '/api/settings/voice-design/1/test')).flush({
        audioBase64: 'UklGRg==',
        contentType: 'audio/wav',
      });
      await stable(fixture);

      expect(el.querySelector('app-voice-design-test r2m-audio-player')).not.toBeNull();
    });

    it('TTS has no test action', async () => {
      const { el } = await render('paragraph-tts', 0);

      expect(el.querySelector('[data-action="test"]')).toBeNull();
    });
  });

  describe('voice-design sample text', () => {
    it('Save is dirty-gated and Reset to default refills the draft', async () => {
      const { fixture, el } = await render('voice-design', 0);
      const save = el.querySelector<HTMLButtonElement>('[data-action="save-sample-text"]')!;
      expect(save.disabled).toBe(true);

      type(el, '[data-field="sampleText"]', 'My own sentence.');
      await stable(fixture);
      expect(save.disabled).toBe(false);
      expect(fixture.componentInstance.hasUnsavedChanges()).toBe(true);
      save.click();
      const put = await eventually('PUT', '/api/settings/voice-design/sample-text');
      expect(put.request.body).toEqual({ text: 'My own sentence.' });
      put.flush({ text: 'My own sentence.', default: 'The default sentence.' });
      await stable(fixture);
      expect(save.disabled).toBe(true);

      el.querySelector<HTMLButtonElement>('[data-action="reset-sample-text"]')!.click();
      await stable(fixture);
      expect(el.querySelector<HTMLTextAreaElement>('[data-field="sampleText"]')?.value).toBe(
        'The default sentence.',
      );
      expect(save.disabled).toBe(false);
    });
  });
});
