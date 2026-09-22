import { AudioPostProcessStepConfig } from '@app/api';
import {
  ADYNEQ_PRESETS,
  ConsonantSoftenForm,
  DEESSER_PRESETS,
  DEFAULT_HIGHPASS_HZ,
  buildConsonantSoftenConfig,
  buildSilenceTrimConfig,
  sameForm,
  setSoftenEngine,
  setSoftenPreset,
  toConsonantSoftenForm,
  toSilenceTrimForm,
  validateAttempts,
  validateChunkPause,
  validateConsonantSoften,
  validatePauses,
  validateSilenceTrim,
  validateWer,
} from './audio-forms';

const STRONG_ADYNEQ = ADYNEQ_PRESETS.strong;

function softenConfig(
  settings: Record<string, unknown>,
  enabled = true,
): AudioPostProcessStepConfig {
  return { stepId: 'consonant-soften', enabled, settings };
}

describe('silence trim form', () => {
  it('seeds from the stored config and falls back to the paragraph defaults', () => {
    expect(toSilenceTrimForm(undefined)).toEqual({ enabled: true, thresholdDb: -50, padMs: 50 });
    expect(
      toSilenceTrimForm({
        stepId: 'silence-trim',
        enabled: false,
        settings: { thresholdDb: -35, padMs: 120, minOutputMs: 200 },
      }),
    ).toEqual({ enabled: false, thresholdDb: -35, padMs: 120 });
  });

  it('builds the same JSON Blazor writes: threshold, clamped pad, the stored output floor', () => {
    const stored = {
      stepId: 'silence-trim',
      enabled: true,
      settings: { thresholdDb: -50, padMs: 50, minOutputMs: 250 },
    };
    const built = buildSilenceTrimConfig({ enabled: false, thresholdDb: -42, padMs: -5 }, stored);
    expect(built).toEqual({
      stepId: 'silence-trim',
      enabled: false,
      settings: { thresholdDb: -42, padMs: 0, minOutputMs: 250 },
    });
    // No stored config at all: the server default floor.
    expect(
      buildSilenceTrimConfig({ enabled: true, thresholdDb: -50, padMs: 50 }, undefined).settings,
    ).toEqual({ thresholdDb: -50, padMs: 50, minOutputMs: 200 });
  });

  it('validates threshold ≤ 0 and pad ≥ 0', () => {
    expect(validateSilenceTrim({ enabled: true, thresholdDb: -50, padMs: 0 })).toBeNull();
    expect(validateSilenceTrim({ enabled: true, thresholdDb: 1, padMs: 0 })).toContain('threshold');
    expect(validateSilenceTrim({ enabled: true, thresholdDb: -50, padMs: -1 })).toContain('Keep');
    expect(validateSilenceTrim({ enabled: true, thresholdDb: NaN, padMs: 0 })).not.toBeNull();
  });
});

describe('consonant soften form', () => {
  it('defaults to adynEQ / strong, seeded from the strong preset, highpass off', () => {
    const form = toConsonantSoftenForm(undefined);
    expect(form.enabled).toBe(false);
    expect(form.engine).toBe('adyneq');
    expect(form.preset).toBe('strong');
    expect(form.adynEq).toEqual(STRONG_ADYNEQ);
    expect(form.deesser).toEqual(DEESSER_PRESETS.strong);
    expect(form.highpassEnabled).toBe(false);
    expect(form.highpassHz).toBe(DEFAULT_HIGHPASS_HZ);
  });

  it('a stored preset seeds the custom drafts from that preset', () => {
    const form = toConsonantSoftenForm(softenConfig({ engine: 'deesser', preset: 'light' }));
    expect(form.engine).toBe('deesser');
    expect(form.preset).toBe('light');
    expect(form.adynEq).toEqual(ADYNEQ_PRESETS.light);
    expect(form.deesser).toEqual(DEESSER_PRESETS.light);
  });

  it('stored custom params win over the seed, and a highpass shows as enabled', () => {
    const form = toConsonantSoftenForm(
      softenConfig({
        engine: 'adyneq',
        preset: 'custom',
        adynEq: { ...STRONG_ADYNEQ, thresholdDb: -28, ratio: 3, highpassHz: 100 },
      }),
    );
    expect(form.preset).toBe('custom');
    expect(form.adynEq.thresholdDb).toBe(-28);
    expect(form.adynEq.ratio).toBe(3);
    // The other engine's draft still seeds from strong (custom has no ladder of its own).
    expect(form.deesser).toEqual(DEESSER_PRESETS.strong);
    expect(form.highpassEnabled).toBe(true);
    expect(form.highpassHz).toBe(100);
  });

  it('picking a preset re-seeds both drafts and clears the highpass — unsaved tweaks are discarded', () => {
    let form = toConsonantSoftenForm(undefined);
    form = {
      ...form,
      adynEq: { ...form.adynEq, ratio: 9 },
      highpassEnabled: true,
      highpassHz: 120,
    };

    form = setSoftenPreset(form, 'medium');
    expect(form.preset).toBe('medium');
    expect(form.adynEq).toEqual(ADYNEQ_PRESETS.medium);
    expect(form.deesser).toEqual(DEESSER_PRESETS.medium);
    expect(form.highpassEnabled).toBe(false);
    expect(form.highpassHz).toBe(DEFAULT_HIGHPASS_HZ);
  });

  it('flipping to custom seeds from the last non-custom preset, and back again re-seeds', () => {
    let form = setSoftenPreset(toConsonantSoftenForm(undefined), 'light');
    form = setSoftenPreset(form, 'custom');
    expect(form.adynEq).toEqual(ADYNEQ_PRESETS.light);

    form = { ...form, adynEq: { ...form.adynEq, thresholdDb: -10 } };
    form = setSoftenPreset(form, 'custom');
    expect(form.adynEq.thresholdDb).toBe(ADYNEQ_PRESETS.light.thresholdDb);
  });

  it('a stored custom config seeds later custom re-selects from strong', () => {
    let form = toConsonantSoftenForm(
      softenConfig({ engine: 'adyneq', preset: 'custom', adynEq: { ...STRONG_ADYNEQ, ratio: 2 } }),
    );
    form = setSoftenPreset(form, 'custom');
    expect(form.adynEq).toEqual(STRONG_ADYNEQ);
  });

  it('changing the engine keeps the drafts', () => {
    let form = toConsonantSoftenForm(undefined);
    form = { ...form, deesser: { ...form.deesser, intensity: 0.9 } };
    form = setSoftenEngine(form, 'deesser');
    expect(form.engine).toBe('deesser');
    expect(form.deesser.intensity).toBe(0.9);
  });

  it('builds a preset reference without raw params for a non-custom preset', () => {
    const form = { ...toConsonantSoftenForm(undefined), enabled: true, preset: 'medium' as const };
    expect(buildConsonantSoftenConfig(form)).toEqual({
      stepId: 'consonant-soften',
      enabled: true,
      settings: { engine: 'adyneq', preset: 'medium' },
    });
  });

  it('builds both raw param sets for custom, with the highpass on each only when enabled', () => {
    let form = setSoftenPreset(toConsonantSoftenForm(undefined), 'custom');
    form = { ...form, highpassEnabled: true, highpassHz: 90 };
    const settings = buildConsonantSoftenConfig(form).settings as Record<string, unknown>;
    expect(settings['preset']).toBe('custom');
    expect(settings['adynEq']).toEqual({ ...STRONG_ADYNEQ, highpassHz: 90 });
    expect(settings['deesser']).toEqual({ ...DEESSER_PRESETS.strong, highpassHz: 90 });
    // Key order matches the host's records so the stored JSON reads the same from either UI.
    expect(Object.keys(settings['adynEq'] as object)).toEqual([
      'thresholdDb',
      'ratio',
      'rangeDb',
      'detectFrequencyHz',
      'detectQ',
      'targetFrequencyHz',
      'targetQ',
      'attackMs',
      'releaseMs',
      'shelfFrequencyHz',
      'shelfGainDb',
      'highpassHz',
    ]);

    form = { ...form, highpassEnabled: false };
    const noHp = buildConsonantSoftenConfig(form).settings as Record<string, unknown>;
    expect(noHp['adynEq']).toEqual(STRONG_ADYNEQ);
    expect('highpassHz' in (noHp['adynEq'] as object)).toBe(false);
  });

  it('validates the custom fields and the highpass range', () => {
    const custom = setSoftenPreset(toConsonantSoftenForm(undefined), 'custom');
    expect(validateConsonantSoften(custom)).toBeNull();
    expect(
      validateConsonantSoften({ ...custom, adynEq: { ...custom.adynEq, ratio: 0.5 } }),
    ).toContain('Ratio');
    expect(
      validateConsonantSoften({ ...custom, adynEq: { ...custom.adynEq, attackMs: -1 } }),
    ).toContain('Attack');
    expect(validateConsonantSoften({ ...custom, highpassEnabled: true, highpassHz: 30 })).toContain(
      'Highpass',
    );
    expect(
      validateConsonantSoften({ ...custom, highpassEnabled: true, highpassHz: 160 }),
    ).toBeNull();
    const deesser = setSoftenEngine(custom, 'deesser');
    expect(
      validateConsonantSoften({ ...deesser, deesser: { ...deesser.deesser, intensity: 1.2 } }),
    ).toContain('Intensity');
    // Non-custom presets never fail on the raw fields — they are not sent.
    expect(
      validateConsonantSoften({
        ...custom,
        preset: 'light',
        adynEq: { ...custom.adynEq, ratio: 0 },
      }),
    ).toBeNull();
  });

  it('sameForm compares structurally', () => {
    const a: ConsonantSoftenForm = toConsonantSoftenForm(undefined);
    expect(sameForm(a, toConsonantSoftenForm(undefined))).toBe(true);
    expect(sameForm(a, { ...a, enabled: true })).toBe(false);
  });
});

describe('scalar card validation', () => {
  it('WER threshold is 0..1', () => {
    expect(validateWer(0)).toBeNull();
    expect(validateWer(1)).toBeNull();
    expect(validateWer(1.01)).not.toBeNull();
    expect(validateWer(-0.1)).not.toBeNull();
  });

  it('chunk pause is ≥ 0 and attempts ≥ 1 whole', () => {
    expect(validateChunkPause(0)).toBeNull();
    expect(validateChunkPause(-1)).not.toBeNull();
    expect(validateAttempts(1)).toBeNull();
    expect(validateAttempts(0)).not.toBeNull();
    expect(validateAttempts(1.5)).not.toBeNull();
  });

  it('every pause must be ≥ 0', () => {
    const ok = { volumeMs: 0, partMs: 1, chapterMs: 2, paragraphMs: 3, pauseMs: 4 };
    expect(validatePauses(ok)).toBeNull();
    expect(validatePauses({ ...ok, chapterMs: -1 })).toContain('Chapter');
  });
});
