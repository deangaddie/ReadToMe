import { afterEach, beforeEach, describe, expect, it } from 'bun:test';
import type { AudioProcessingFull, RecentAudioSample, StepPreviewResponse } from '@app/api';
import { override, resetServices } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import { FakeApi, problem } from '../../../../testing/fake-api';
import { FakeLive } from '../../../../testing/fake-live';
import { settle } from '../../../../testing/fake-navigation';
import { ADYNEQ_PRESETS } from './audio-forms';
import type { AudioSettingsPage } from './audio-settings-page';
import { SamplePicker } from './sample-picker-dialog';
import './audio-settings-page';

/** Ported from the Angular TestBed spec, case for case, over `FakeApi`, `FakeLive` and a recorded picker. */
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

let api: FakeApi;
let live: FakeLive;
let nextSample: RecentAudioSample | null;
let picks: number;
let toasts: string[];

beforeEach(() => {
  resetServices();
  api = new FakeApi();
  api.install();
  live = new FakeLive().install();
  nextSample = null;
  picks = 0;
  toasts = [];
  override(SamplePicker, {
    pick: async () => {
      picks++;
      return nextSample;
    },
  } as unknown as SamplePicker);
  override(ToastService, {
    success: (m: string) => toasts.push(m),
    error: (m: string) => toasts.push(m),
    problem: () => undefined,
  } as unknown as ToastService);
});
afterEach(() => {
  document.body.replaceChildren();
  // Angular's `http.verify()`: nothing the page asked for went unanswered.
  expect(api.unrouted).toEqual([]);
});

async function flush(page: AudioSettingsPage) {
  for (let i = 0; i < 4; i++) await settle();
  await page.rendered();
  await page.rendered();
}

async function render(snapshot = full()) {
  api.on('GET', FULL, snapshot);
  const page = document.createElement('r2m-audio-settings-page') as AudioSettingsPage;
  document.body.append(page);
  await flush(page);
  return page;
}

const card = (el: HTMLElement, id: string) => el.querySelector<HTMLElement>(`[data-card="${id}"]`)!;
const field = (el: HTMLElement, key: string) =>
  el.querySelector<HTMLInputElement>(`[data-field="${key}"]`)!;
const saveButton = (el: HTMLElement, id: string) =>
  card(el, id).querySelector<HTMLButtonElement>('[data-action="save"]')!;

async function type(page: AudioSettingsPage, key: string, value: string) {
  const input = field(page, key);
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await page.rendered();
}

/** Picks an option as a user would: the native select's value, then `change`. */
async function selectValue(page: AudioSettingsPage, key: string, value: string) {
  const select = page.querySelector<HTMLSelectElement>(`[data-field="${key}"]`)!;
  select.value = value;
  select.dispatchEvent(new Event('change'));
  await page.rendered();
}

describe('r2m-audio-settings-page', () => {
  it('renders the seven cards seeded from the full snapshot, every Save disabled', async () => {
    const page = await render();

    expect(
      Array.from(page.querySelectorAll('[data-card]')).map((c) => c.getAttribute('data-card')),
    ).toEqual([
      'ffmpeg',
      'silence-trim',
      'consonant-soften',
      'chunk-pause',
      'pauses',
      'wer',
      'attempts',
    ]);
    expect(field(page, 'ffmpegPath').value).toBe('D:\\ffmpeg\\bin\\ffmpeg.exe');
    expect(field(page, 'trim-threshold').value).toBe('-50');
    expect(field(page, 'trim-pad').value).toBe('50');
    expect(field(page, 'chunkPauseMs').value).toBe('300');
    expect(field(page, 'chapterMs').value).toBe('2500');
    expect(field(page, 'werThreshold').value).toBe('0.15');
    expect(field(page, 'audioMaxAttempts').value).toBe('1');
    expect(
      Array.from(page.querySelectorAll<HTMLButtonElement>('[data-action="save"]')).map(
        (b) => b.disabled,
      ),
    ).toEqual([true, true, true, true, true, true, true]);
    // Strong preset: no custom fields, no deesser caveat.
    expect(page.querySelector('[data-role="custom-fields"]')).toBeNull();
    expect(page.querySelector('[data-role="deesser-caveat"]')).toBeNull();
    expect(page.hasUnsavedChanges()).toBe(false);
  });

  it('saves the WER threshold on its own, then reloads; out-of-range values block Save', async () => {
    const page = await render();

    await type(page, 'werThreshold', '1.5');
    expect(card(page, 'wer').querySelector('[data-role="card-error"]')?.textContent).toContain(
      'between 0 and 1',
    );
    expect(saveButton(page, 'wer').disabled).toBe(true);

    await type(page, 'werThreshold', '0.2');
    expect(card(page, 'wer').querySelector('[data-role="card-error"]')).toBeNull();
    expect(saveButton(page, 'wer').disabled).toBe(false);
    expect(page.hasUnsavedChanges()).toBe(true);
    // Other cards stay clean.
    expect(saveButton(page, 'attempts').disabled).toBe(true);

    api.on('PUT', BASE, {});
    api.on('GET', FULL, full({ werThreshold: 0.2 }));
    saveButton(page, 'wer').click();
    await flush(page);

    expect(api.calls('PUT', BASE)[0]?.body).toEqual({ werThreshold: 0.2 });
    expect(saveButton(page, 'wer').disabled).toBe(true);
    expect(field(page, 'werThreshold').value).toBe('0.2');
    expect(toasts).toEqual(['WER threshold saved']);
  });

  it('a failed save keeps the edit, dirty, through the next reload', async () => {
    const page = await render();
    await type(page, 'werThreshold', '0.4');

    api.on('PUT', BASE, problem(500, 'disk full'));
    saveButton(page, 'wer').click();
    await flush(page);
    expect(field(page, 'werThreshold').value).toBe('0.4');
    expect(saveButton(page, 'wer').disabled).toBe(false);

    // The host did not change, so a reload must not treat the failed edit as saved and drop it.
    live.emit('settingsChanged', { area: 'audio-processing' });
    await flush(page);
    expect(field(page, 'werThreshold').value).toBe('0.4');
    expect(page.hasUnsavedChanges()).toBe(true);
  });

  it('saves the five pauses together and refuses a negative one', async () => {
    const page = await render();

    await type(page, 'chapterMs', '-5');
    expect(card(page, 'pauses').querySelector('[data-role="card-error"]')?.textContent).toContain(
      'Chapter pause',
    );

    await type(page, 'chapterMs', '2600');
    api.on('PUT', `${BASE}/pauses`, (body) => body);
    saveButton(page, 'pauses').click();
    await flush(page);
    expect(api.calls('PUT', `${BASE}/pauses`)[0]?.body).toEqual({
      volumeMs: 4000,
      partMs: 3000,
      chapterMs: 2600,
      paragraphMs: 800,
      pauseMs: 500,
    });
    expect(api.calls('GET', FULL).length).toBe(2);
  });

  it('saves silence trim as a step config carrying the stored output floor', async () => {
    const page = await render();

    await type(page, 'trim-threshold', '-42');
    api.on('PUT', `${BASE}/steps/silence-trim`, (body) => body);
    saveButton(page, 'silence-trim').click();
    await flush(page);
    expect(api.calls('PUT', `${BASE}/steps/silence-trim`)[0]?.body).toEqual({
      stepId: 'silence-trim',
      enabled: true,
      settings: { thresholdDb: -42, padMs: 50, minOutputMs: 200 },
    });
  });

  it('a hub refresh reseeds untouched cards and leaves typing in progress alone', async () => {
    const page = await render();

    await type(page, 'werThreshold', '0.3');

    api.on('GET', FULL, full({ werThreshold: 0.25, chunkPauseMs: 450, ffmpegPath: null }));
    live.emit('settingsChanged', { area: 'audio-processing' });
    await flush(page);

    expect(field(page, 'werThreshold').value).toBe('0.3'); // the edit survives
    expect(saveButton(page, 'wer').disabled).toBe(false);
    expect(field(page, 'chunkPauseMs').value).toBe('450'); // untouched card follows the host
    expect(field(page, 'ffmpegPath').value).toBe('');
  });

  it('other areas do not trigger a reload', async () => {
    const page = await render();
    live.emit('settingsChanged', { area: 'llm' });
    await flush(page);
    expect(api.calls('GET', FULL).length).toBe(1);
  });

  it('stops listening while detached and listens again once re-attached', async () => {
    const page = await render();
    page.remove();
    live.emit('settingsChanged', { area: 'audio-processing' });
    await settle();
    expect(api.calls('GET', FULL).length).toBe(1);

    document.body.append(page);
    await flush(page);
    expect(api.calls('GET', FULL).length).toBe(2);
  });

  it('custom preset shows the engine fields; picking a preset re-seeds them', async () => {
    const page = await render();

    await selectValue(page, 'soften-preset', 'custom');
    expect(page.querySelector('[data-role="custom-fields"]')).not.toBeNull();
    expect(field(page, 'adyneq-ratio').value).toBe(String(ADYNEQ_PRESETS.strong.ratio));
    expect(page.querySelector('[data-field="soften-highpass-hz"]')).toBeNull();

    await type(page, 'adyneq-ratio', '9');
    expect(field(page, 'adyneq-ratio').value).toBe('9');

    await selectValue(page, 'soften-preset', 'light');
    expect(page.querySelector('[data-role="custom-fields"]')).toBeNull();

    await selectValue(page, 'soften-preset', 'custom');
    expect(field(page, 'adyneq-ratio').value).toBe(String(ADYNEQ_PRESETS.light.ratio));

    // The deesser engine shows its own fields and the sample-rate caveat.
    await selectValue(page, 'soften-engine', 'deesser');
    expect(page.querySelector('[data-role="deesser-caveat"]')).not.toBeNull();
    expect(field(page, 'deesser-intensity')).not.toBeNull();
    expect(page.querySelector('[data-field="adyneq-ratio"]')).toBeNull();

    api.on('PUT', `${BASE}/steps/consonant-soften`, (body) => body);
    saveButton(page, 'consonant-soften').click();
    await flush(page);
    expect(api.calls('PUT', `${BASE}/steps/consonant-soften`)[0]?.body).toMatchObject({
      stepId: 'consonant-soften',
      enabled: false,
      settings: {
        engine: 'deesser',
        preset: 'custom',
        adynEq: ADYNEQ_PRESETS.light,
        deesser: { intensity: 0.35, makeupAmount: 0.5 },
      },
    });
  });

  it('the highpass switch adds its cutoff field and the saved params carry it', async () => {
    const page = await render();
    await selectValue(page, 'soften-preset', 'custom');

    const highpass = field(page, 'soften-highpass');
    highpass.checked = true;
    highpass.dispatchEvent(new Event('change'));
    await page.rendered();
    expect(field(page, 'soften-highpass-hz').value).toBe('80');

    await type(page, 'soften-highpass-hz', '20');
    expect(
      card(page, 'consonant-soften').querySelector('[data-role="card-error"]')?.textContent,
    ).toContain('between 40 and 160');

    await type(page, 'soften-highpass-hz', '100');
    api.on('PUT', `${BASE}/steps/consonant-soften`, (body) => body);
    saveButton(page, 'consonant-soften').click();
    await flush(page);
    expect(api.calls('PUT', `${BASE}/steps/consonant-soften`)[0]?.body).toMatchObject({
      settings: { adynEq: { ...ADYNEQ_PRESETS.strong, highpassHz: 100 } },
    });
  });

  it('renders an A/B preview of the unsaved draft over the picked sample', async () => {
    const page = await render();
    const preview = card(page, 'silence-trim').querySelector<HTMLElement>('r2m-step-preview')!;
    const pick = preview.querySelector<HTMLButtonElement>('[data-action="pick-sample"]')!;
    const renderButton = preview.querySelector<HTMLButtonElement>(
      '[data-action="render-preview"]',
    )!;
    expect(renderButton.disabled).toBe(true);

    nextSample = SAMPLE;
    pick.click();
    await flush(page);
    expect(picks).toBe(1);
    expect(preview.querySelector('[data-role="sample"]')?.textContent).toContain(
      'Hardin · Hardin Voice · Foundation',
    );
    expect(pick.textContent?.trim()).toBe('Change sample');
    expect(renderButton.disabled).toBe(false);

    await type(page, 'trim-threshold', '-40');
    const response: StepPreviewResponse = {
      previewId: 'p1',
      originalUrl: '/preview-source/foundation/item-1',
      processedUrl: '/audio-preview/p1',
      removedMs: 312.4,
      reason: null,
      appliedOk: true,
    };
    api.on('POST', `${BASE}/steps/silence-trim/preview`, response);
    renderButton.click();
    await flush(page);

    expect(api.calls('POST', `${BASE}/steps/silence-trim/preview`)[0]?.body).toEqual({
      sample: { folder: 'foundation', itemId: 'item-1' },
      settings: { thresholdDb: -40, padMs: 50, minOutputMs: 200 },
    });
    const audio = preview.querySelector('audio')!;
    expect(audio.getAttribute('src')).toBe('/preview-source/foundation/item-1?v=p1');
    const ab = preview.querySelectorAll('.r2m-audio-player__ab-btn');
    expect(Array.from(ab).map((b) => b.textContent?.trim())).toEqual(['Original', 'Trimmed']);
    expect(preview.querySelector('[data-role="removed"]')?.textContent).toContain('Removed 312 ms');
    expect(preview.querySelector('[data-role="reason"]')).toBeNull();
    // Nothing was saved by previewing.
    expect(saveButton(page, 'silence-trim').disabled).toBe(false);
    expect(api.calls('PUT', `${BASE}/steps/silence-trim`)).toEqual([]);
  });

  it('a fallen-back render shows the reason', async () => {
    const page = await render();
    const preview = card(page, 'consonant-soften').querySelector<HTMLElement>('r2m-step-preview')!;
    nextSample = SAMPLE;
    preview.querySelector<HTMLButtonElement>('[data-action="pick-sample"]')!.click();
    await flush(page);
    api.on('POST', `${BASE}/steps/consonant-soften/preview`, {
      previewId: 'p2',
      originalUrl: '/o',
      processedUrl: '/p',
      removedMs: null,
      reason: 'ffmpeg exited 1',
      appliedOk: false,
    });
    preview.querySelector<HTMLButtonElement>('[data-action="render-preview"]')!.click();
    await flush(page);
    expect(api.calls('POST', `${BASE}/steps/consonant-soften/preview`)[0]?.body).toEqual({
      sample: { folder: 'foundation', itemId: 'item-1' },
      settings: { engine: 'adyneq', preset: 'strong' },
    });
    expect(preview.querySelector('[data-role="reason"]')?.textContent).toContain('ffmpeg exited 1');
    expect(preview.querySelector('[data-role="removed"]')).toBeNull();
  });

  it('Test ffmpeg posts the path in the field, then reloads', async () => {
    const page = await render();
    await type(page, 'ffmpegPath', 'C:\\tools\\ffmpeg.exe');
    api.on('POST', `${BASE}/ffmpeg/test`, { success: true, message: 'ffmpeg version 7.1' });
    api.on('GET', FULL, full({ ffmpegPath: 'C:\\tools\\ffmpeg.exe' }));
    card(page, 'ffmpeg').querySelector<HTMLButtonElement>('[data-action="test-ffmpeg"]')!.click();
    await flush(page);
    expect(api.calls('POST', `${BASE}/ffmpeg/test`)[0]?.body).toEqual({
      ffmpegPath: 'C:\\tools\\ffmpeg.exe',
    });
    // The test persisted the path, so the card is clean against the reloaded value.
    expect(saveButton(page, 'ffmpeg').disabled).toBe(true);
    expect(toasts).toEqual(['ffmpeg version 7.1']);
  });
});
