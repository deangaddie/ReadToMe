import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSelect } from '@angular/material/select';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { AudioProcessingFull, RecentAudioSample, StepPreviewResponse } from '@app/api';
import { LiveService } from '@app/live/live.service';
import { SettingsChangedMessage } from '@app/live/live-messages';
import { Subject } from 'rxjs';
import { ADYNEQ_PRESETS } from './audio-forms';
import { AudioSettingsPage } from './audio-settings-page';
import { SamplePicker } from './sample-picker-dialog';

class FakeLive {
  readonly settingsChanged = new Subject<SettingsChangedMessage>();
  on(family: string) {
    if (family !== 'settingsChanged') throw new Error(`unexpected family ${family}`);
    return this.settingsChanged.asObservable();
  }
}

class FakePicker {
  next: RecentAudioSample | null = null;
  pick = vi.fn(async () => this.next);
}

const BASE = '/api/settings/audio-processing';
const FULL = `${BASE}/full`;

const SAMPLE: RecentAudioSample = {
  itemId: 'item-1',
  folder: 'foundation',
  text: 'The Encyclopedia Galactica is a marvel of the age.',
  characterName: 'Hardin',
  voiceName: 'Hardin Voice',
  projectTitle: 'Foundation',
};

function full(overrides: Partial<AudioProcessingFull> = {}): AudioProcessingFull {
  return {
    ffmpegPath: 'D:\\ffmpeg\\bin\\ffmpeg.exe',
    werThreshold: 0.15,
    sentenceSplitEnabled: false,
    chunkPauseMs: 300,
    audioMaxAttempts: 1,
    pauses: { volumeMs: 4000, partMs: 3000, chapterMs: 2500, paragraphMs: 800, pauseMs: 500 },
    steps: [
      {
        stepId: 'silence-trim',
        enabled: true,
        settings: { thresholdDb: -50, padMs: 50, minOutputMs: 200 },
      },
      {
        stepId: 'consonant-soften',
        enabled: false,
        settings: { engine: 'adyneq', preset: 'strong' },
      },
    ],
    ...overrides,
  };
}

describe('AudioSettingsPage', () => {
  let http: HttpTestingController;
  let live: FakeLive;
  let picker: FakePicker;

  beforeEach(async () => {
    live = new FakeLive();
    picker = new FakePicker();
    await TestBed.configureTestingModule({
      imports: [AudioSettingsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: LiveService, useValue: live },
        { provide: SamplePicker, useValue: picker },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

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

  async function render(snapshot = full()) {
    const fixture = TestBed.createComponent(AudioSettingsPage);
    (await eventually('GET', FULL)).flush(snapshot);
    await stable(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const card = (el: HTMLElement, id: string) =>
    el.querySelector<HTMLElement>(`[data-card="${id}"]`)!;
  const field = (el: HTMLElement, key: string) =>
    el.querySelector<HTMLInputElement>(`[data-field="${key}"]`)!;
  const saveButton = (el: HTMLElement, id: string) =>
    card(el, id).querySelector<HTMLButtonElement>('[data-action="save"]')!;

  function type(input: HTMLInputElement, value: string) {
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function selectValue(fixture: ComponentFixture<unknown>, key: string, value: string) {
    const select = fixture.debugElement.query(By.css(`[data-field="${key}"]`))
      .componentInstance as MatSelect;
    select.valueChange.emit(value);
  }

  it('renders the seven cards seeded from the full snapshot, every Save disabled', async () => {
    const { el } = await render();

    expect(
      Array.from(el.querySelectorAll('[data-card]')).map((c) => c.getAttribute('data-card')),
    ).toEqual([
      'ffmpeg',
      'silence-trim',
      'consonant-soften',
      'chunk-pause',
      'pauses',
      'wer',
      'attempts',
    ]);
    expect(field(el, 'ffmpegPath').value).toBe('D:\\ffmpeg\\bin\\ffmpeg.exe');
    expect(field(el, 'trim-threshold').value).toBe('-50');
    expect(field(el, 'trim-pad').value).toBe('50');
    expect(field(el, 'chunkPauseMs').value).toBe('300');
    expect(field(el, 'chapterMs').value).toBe('2500');
    expect(field(el, 'werThreshold').value).toBe('0.15');
    expect(field(el, 'audioMaxAttempts').value).toBe('1');
    expect(
      Array.from(el.querySelectorAll<HTMLButtonElement>('[data-action="save"]')).map(
        (b) => b.disabled,
      ),
    ).toEqual([true, true, true, true, true, true, true]);
    // Strong preset: no custom fields, no deesser caveat.
    expect(el.querySelector('[data-role="custom-fields"]')).toBeNull();
    expect(el.querySelector('[data-role="deesser-caveat"]')).toBeNull();
  });

  it('saves the WER threshold on its own, then reloads; out-of-range values block Save', async () => {
    const { fixture, el } = await render();

    type(field(el, 'werThreshold'), '1.5');
    await stable(fixture);
    expect(card(el, 'wer').querySelector('[data-role="card-error"]')?.textContent).toContain(
      'between 0 and 1',
    );
    expect(saveButton(el, 'wer').disabled).toBe(true);

    type(field(el, 'werThreshold'), '0.2');
    await stable(fixture);
    expect(card(el, 'wer').querySelector('[data-role="card-error"]')).toBeNull();
    expect(saveButton(el, 'wer').disabled).toBe(false);
    // Other cards stay clean.
    expect(saveButton(el, 'attempts').disabled).toBe(true);

    saveButton(el, 'wer').click();
    const put = await eventually('PUT', BASE);
    expect(put.request.body).toEqual({ werThreshold: 0.2 });
    put.flush({});
    (await eventually('GET', FULL)).flush(full({ werThreshold: 0.2 }));
    await stable(fixture);
    expect(saveButton(el, 'wer').disabled).toBe(true);
    expect(field(el, 'werThreshold').value).toBe('0.2');
  });

  it('saves the five pauses together and refuses a negative one', async () => {
    const { fixture, el } = await render();

    type(field(el, 'chapterMs'), '-5');
    await stable(fixture);
    expect(card(el, 'pauses').querySelector('[data-role="card-error"]')?.textContent).toContain(
      'Chapter pause',
    );

    type(field(el, 'chapterMs'), '2600');
    await stable(fixture);
    saveButton(el, 'pauses').click();
    const put = await eventually('PUT', `${BASE}/pauses`);
    expect(put.request.body).toEqual({
      volumeMs: 4000,
      partMs: 3000,
      chapterMs: 2600,
      paragraphMs: 800,
      pauseMs: 500,
    });
    put.flush(put.request.body);
    (await eventually('GET', FULL)).flush(full());
  });

  it('saves silence trim as a step config carrying the stored output floor', async () => {
    const { fixture, el } = await render();

    type(field(el, 'trim-threshold'), '-42');
    await stable(fixture);
    saveButton(el, 'silence-trim').click();
    const put = await eventually('PUT', `${BASE}/steps/silence-trim`);
    expect(put.request.body).toEqual({
      stepId: 'silence-trim',
      enabled: true,
      settings: { thresholdDb: -42, padMs: 50, minOutputMs: 200 },
    });
    put.flush(put.request.body);
    (await eventually('GET', FULL)).flush(full());
  });

  it('a hub refresh reseeds untouched cards and leaves typing in progress alone', async () => {
    const { fixture, el } = await render();

    type(field(el, 'werThreshold'), '0.3');
    await stable(fixture);

    live.settingsChanged.next({ area: 'audio-processing' });
    (await eventually('GET', FULL)).flush(
      full({ werThreshold: 0.25, chunkPauseMs: 450, ffmpegPath: null }),
    );
    await stable(fixture);

    expect(field(el, 'werThreshold').value).toBe('0.3'); // the edit survives
    expect(saveButton(el, 'wer').disabled).toBe(false);
    expect(field(el, 'chunkPauseMs').value).toBe('450'); // untouched card follows the host
    expect(field(el, 'ffmpegPath').value).toBe('');
  });

  it('other areas do not trigger a reload', async () => {
    await render();
    live.settingsChanged.next({ area: 'llm' });
    await settle(20);
    expect(http.match(() => true)).toEqual([]);
  });

  it('custom preset shows the engine fields; picking a preset re-seeds them', async () => {
    const { fixture, el } = await render();

    selectValue(fixture, 'soften-preset', 'custom');
    await stable(fixture);
    expect(el.querySelector('[data-role="custom-fields"]')).not.toBeNull();
    expect(field(el, 'adyneq-ratio').value).toBe(String(ADYNEQ_PRESETS.strong.ratio));
    expect(el.querySelector('[data-field="soften-highpass-hz"]')).toBeNull();

    type(field(el, 'adyneq-ratio'), '9');
    await stable(fixture);
    expect(field(el, 'adyneq-ratio').value).toBe('9');

    selectValue(fixture, 'soften-preset', 'light');
    await stable(fixture);
    expect(el.querySelector('[data-role="custom-fields"]')).toBeNull();

    selectValue(fixture, 'soften-preset', 'custom');
    await stable(fixture);
    expect(field(el, 'adyneq-ratio').value).toBe(String(ADYNEQ_PRESETS.light.ratio));

    // The deesser engine shows its own fields and the sample-rate caveat.
    selectValue(fixture, 'soften-engine', 'deesser');
    await stable(fixture);
    expect(el.querySelector('[data-role="deesser-caveat"]')).not.toBeNull();
    expect(field(el, 'deesser-intensity')).not.toBeNull();
    expect(el.querySelector('[data-field="adyneq-ratio"]')).toBeNull();

    saveButton(el, 'consonant-soften').click();
    const put = await eventually('PUT', `${BASE}/steps/consonant-soften`);
    expect(put.request.body).toMatchObject({
      stepId: 'consonant-soften',
      enabled: false,
      settings: {
        engine: 'deesser',
        preset: 'custom',
        adynEq: ADYNEQ_PRESETS.light,
        deesser: { intensity: 0.35, makeupAmount: 0.5 },
      },
    });
    put.flush(put.request.body);
    (await eventually('GET', FULL)).flush(full());
  });

  it('renders an A/B preview of the unsaved draft over the picked sample', async () => {
    const { fixture, el } = await render();
    const preview = card(el, 'silence-trim').querySelector<HTMLElement>('app-step-preview')!;
    const pick = preview.querySelector<HTMLButtonElement>('[data-action="pick-sample"]')!;
    const renderButton = preview.querySelector<HTMLButtonElement>(
      '[data-action="render-preview"]',
    )!;
    expect(renderButton.disabled).toBe(true);

    picker.next = SAMPLE;
    pick.click();
    await stable(fixture);
    expect(preview.querySelector('[data-role="sample"]')?.textContent).toContain(
      'Hardin · Hardin Voice · Foundation',
    );
    expect(pick.textContent?.trim()).toBe('Change sample');
    expect(renderButton.disabled).toBe(false);

    type(field(el, 'trim-threshold'), '-40');
    await stable(fixture);
    renderButton.click();
    const post = await eventually('POST', `${BASE}/steps/silence-trim/preview`);
    expect(post.request.body).toEqual({
      sample: { folder: 'foundation', itemId: 'item-1' },
      settings: { thresholdDb: -40, padMs: 50, minOutputMs: 200 },
    });
    const response: StepPreviewResponse = {
      previewId: 'p1',
      originalUrl: '/preview-source/foundation/item-1',
      processedUrl: '/audio-preview/p1',
      removedMs: 312.4,
      reason: null,
      appliedOk: true,
    };
    post.flush(response);
    await stable(fixture);

    const audio = preview.querySelector('audio')!;
    expect(audio.getAttribute('src')).toBe('/preview-source/foundation/item-1?v=p1');
    const ab = preview.querySelectorAll('.r2m-audio-player__ab-btn');
    expect(Array.from(ab).map((b) => b.textContent?.trim())).toEqual(['Original', 'Trimmed']);
    expect(preview.querySelector('[data-role="removed"]')?.textContent).toContain('Removed 312 ms');
    expect(preview.querySelector('[data-role="reason"]')).toBeNull();
    // Nothing was saved by previewing.
    expect(saveButton(el, 'silence-trim').disabled).toBe(false);
  });

  it('a fallen-back render shows the reason', async () => {
    const { fixture, el } = await render();
    const preview = card(el, 'consonant-soften').querySelector<HTMLElement>('app-step-preview')!;
    picker.next = SAMPLE;
    preview.querySelector<HTMLButtonElement>('[data-action="pick-sample"]')!.click();
    await stable(fixture);
    preview.querySelector<HTMLButtonElement>('[data-action="render-preview"]')!.click();
    const post = await eventually('POST', `${BASE}/steps/consonant-soften/preview`);
    expect(post.request.body).toEqual({
      sample: { folder: 'foundation', itemId: 'item-1' },
      settings: { engine: 'adyneq', preset: 'strong' },
    });
    post.flush({
      previewId: 'p2',
      originalUrl: '/o',
      processedUrl: '/p',
      removedMs: null,
      reason: 'ffmpeg exited 1',
      appliedOk: false,
    });
    await stable(fixture);
    expect(preview.querySelector('[data-role="reason"]')?.textContent).toContain('ffmpeg exited 1');
    expect(preview.querySelector('[data-role="removed"]')).toBeNull();
  });

  it('Test ffmpeg posts the path in the field, then reloads', async () => {
    const { fixture, el } = await render();
    type(field(el, 'ffmpegPath'), 'C:\\tools\\ffmpeg.exe');
    await stable(fixture);
    card(el, 'ffmpeg').querySelector<HTMLButtonElement>('[data-action="test-ffmpeg"]')!.click();
    const post = await eventually('POST', `${BASE}/ffmpeg/test`);
    expect(post.request.body).toEqual({ ffmpegPath: 'C:\\tools\\ffmpeg.exe' });
    post.flush({ success: true, message: 'ffmpeg version 7.1' });
    (await eventually('GET', FULL)).flush(full({ ffmpegPath: 'C:\\tools\\ffmpeg.exe' }));
    await stable(fixture);
    // The test persisted the path, so the card is clean against the reloaded value.
    expect(saveButton(el, 'ffmpeg').disabled).toBe(true);
    expect(document.querySelector('.r2m-toast-panel')?.textContent).toContain('ffmpeg version 7.1');
  });
});
