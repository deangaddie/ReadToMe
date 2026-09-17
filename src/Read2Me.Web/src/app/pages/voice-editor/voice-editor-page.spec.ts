import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PreviewResponse, StepCatalogEntryDto, VoiceDto } from '@app/api';
import { VoiceEditorPage } from './voice-editor-page';

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

const settle = (ms = 0) => new Promise((r) => setTimeout(r, ms));

describe('VoiceEditorPage', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<VoiceEditorPage>;

  const el = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const button = (action: string) =>
    el().querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [VoiceEditorPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VoiceEditorPage);
    fixture.componentRef.setInput('folder', 'dune');
    fixture.componentRef.setInput('voiceId', 'v1');
  });

  afterEach(() => {
    http.verify();
    document.querySelectorAll('.cdk-overlay-container').forEach((n) => n.remove());
  });

  async function load(voice: VoiceDto | null = VOICE): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    const voiceReq = http.expectOne('/api/projects/dune/voices/v1');
    if (voice) voiceReq.flush(voice);
    else voiceReq.flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    http.expectOne((r) => r.url === '/api/audio/steps/catalog').flush(CATALOG);
    await settle();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function tick(stepId: string): Promise<void> {
    el().querySelector<HTMLInputElement>(`[data-tick="${stepId}"] input[type=checkbox]`)!.click();
    await settle();
    fixture.detectChanges();
  }

  async function previewAndFlush(): Promise<void> {
    button('preview').click();
    await settle();
    const req = http.expectOne('/api/projects/dune/voices/v1/editor/preview');
    expect(req.request.body).toEqual({
      steps: [
        { stepId: 'denoise', settings: { strength: 20 } },
        { stepId: 'silence-trim', settings: { thresholdDb: -35 } },
      ],
    });
    req.flush(PREVIEW);
    await settle();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('lists the catalog steps, plays the live WAV as Original, and gates Preview and Apply', async () => {
    await load();

    expect(
      Array.from(el().querySelectorAll('[data-step]')).map((n) => n.getAttribute('data-step')),
    ).toEqual(['denoise', 'hiss-reduce', 'silence-trim']);
    expect(el().querySelector('[data-testid="original-player"] audio')?.getAttribute('src')).toBe(
      '/workspace/dune/voices/alice/v1-main.wav?v=0',
    );
    expect(button('preview').disabled).toBe(true);
    expect(button('apply').disabled).toBe(true);
    expect(el().querySelector('[data-action="restore"]')).toBeNull();
  });

  it('renders the ticked steps in catalog order and stacks an After player per stage', async () => {
    await load();
    await tick('silence-trim');
    await tick('denoise');
    expect(button('preview').disabled).toBe(false);

    await previewAndFlush();

    const stages = Array.from(el().querySelectorAll('[data-stage]'));
    expect(stages.map((n) => n.getAttribute('data-stage'))).toEqual(['denoise', 'silence-trim']);
    expect(stages[1]!.querySelector('audio')?.getAttribute('src')).toContain(
      '/api/previews/p1/silence-trim.wav',
    );
    expect(el().querySelector('[data-testid="skipped-chip"]')?.textContent).toContain(
      'Skipped — trimmed result under 1000 ms',
    );
    expect(button('apply').disabled).toBe(false);
  });

  it('stales the render when a dial changes after Preview, until the next Preview', async () => {
    await load();
    await tick('silence-trim');
    await tick('denoise');
    await previewAndFlush();
    expect(button('apply').disabled).toBe(false);

    const strength = el().querySelector<HTMLInputElement>(
      '[data-key="strength"] input[type="number"]',
    )!;
    strength.value = '31';
    strength.dispatchEvent(new Event('input'));
    await settle();
    fixture.detectChanges();

    expect(button('apply').disabled).toBe(true);
    expect(el().querySelector('[data-testid="stale-hint"]')).not.toBeNull();

    button('preview').click();
    await settle();
    const req = http.expectOne('/api/projects/dune/voices/v1/editor/preview');
    expect(req.request.body.steps[0]).toEqual({ stepId: 'denoise', settings: { strength: 31 } });
    req.flush({ ...PREVIEW, previewId: 'p2' });
    await settle();
    fixture.detectChanges();
    expect(button('apply').disabled).toBe(false);
    expect(el().querySelector('[data-testid="stale-hint"]')).toBeNull();
  });

  it('stales the render when a tick changes', async () => {
    await load();
    await tick('silence-trim');
    await tick('denoise');
    await previewAndFlush();

    await tick('hiss-reduce');

    expect(button('apply').disabled).toBe(true);
    expect(el().querySelector('[data-testid="hiss-hint"]')).not.toBeNull();
  });

  it('applies the heard previewId and switches Original to the stored original', async () => {
    await load();
    await tick('silence-trim');
    await tick('denoise');
    await previewAndFlush();

    button('apply').click();
    await settle();
    const req = http.expectOne('/api/projects/dune/voices/v1/editor/apply');
    expect(req.request.body).toEqual({ previewId: 'p1' });
    req.flush({ ...VOICE, isEdited: true });
    await settle();
    fixture.detectChanges();

    expect(el().querySelector('[data-testid="edited-chip"]')).not.toBeNull();
    expect(el().querySelector('[data-action="restore"]')).not.toBeNull();
    expect(el().querySelector('[data-testid="original-player"] audio')?.getAttribute('src')).toBe(
      '/api/projects/dune/voices/v1/original.wav?v=1',
    );
  });

  it('shows a message and a way back when the voice is gone', async () => {
    await load(null);

    expect(el().querySelector('r2m-empty-state')?.textContent).toContain(
      'That voice no longer exists.',
    );
    expect(el().querySelector('[data-action="back-empty"]')).not.toBeNull();
  });

  it('refuses a voice without audio', async () => {
    await load({ ...VOICE, audioFileName: null });

    expect(el().querySelector('r2m-empty-state')?.textContent).toContain(
      'This voice has no audio to edit.',
    );
  });
});
