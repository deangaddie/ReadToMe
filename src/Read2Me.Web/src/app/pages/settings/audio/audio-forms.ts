import {
  AUDIO_STEP_IDS,
  AdynEqParams,
  AudioPostProcessStepConfig,
  ConsonantSoftenEngine,
  ConsonantSoftenPreset,
  ConsonantSoftenSettings,
  DeesserParams,
  PauseDurations,
  SilenceTrimSettings,
} from '@app/api';

/**
 * Edit-state and rules for the audio-processing cards (ticket 24), kept pure so the preset
 * re-seed rules and the per-card validation can be tested without a component. Mirrors
 * Blazor's `SilenceTrimForm` / `ConsonantSoftenForm`: presets are stored by reference, so raw
 * params are only written when the preset is custom, and picking any preset (custom included)
 * re-seeds the drafts from the last non-custom preset, discarding unsaved tweaks.
 */

// ---- silence trim ---------------------------------------------------------------------------------

export interface SilenceTrimForm {
  enabled: boolean;
  thresholdDb: number;
  padMs: number;
}

/** The paragraph-scope defaults (`AudioPostProcessStepDefaults`, `SilenceTrimSettings`). */
const SILENCE_TRIM_DEFAULTS: Required<SilenceTrimSettings> = {
  thresholdDb: -50,
  padMs: 50,
  minOutputMs: 200,
};

function trimSettings(
  config: AudioPostProcessStepConfig | undefined,
): Required<SilenceTrimSettings> {
  return { ...SILENCE_TRIM_DEFAULTS, ...((config?.settings as SilenceTrimSettings | null) ?? {}) };
}

export function toSilenceTrimForm(config: AudioPostProcessStepConfig | undefined): SilenceTrimForm {
  const settings = trimSettings(config);
  return {
    enabled: config?.enabled ?? true,
    thresholdDb: settings.thresholdDb,
    padMs: settings.padMs,
  };
}

/**
 * The config to save or preview. The output floor is not a form field: it carries through from
 * the stored config (or the server default) so a save never silently changes it.
 */
export function buildSilenceTrimConfig(
  form: SilenceTrimForm,
  stored: AudioPostProcessStepConfig | undefined,
): AudioPostProcessStepConfig {
  const settings: SilenceTrimSettings = {
    thresholdDb: form.thresholdDb,
    padMs: Math.max(0, form.padMs),
    minOutputMs: trimSettings(stored).minOutputMs,
  };
  return { stepId: AUDIO_STEP_IDS.silenceTrim, enabled: form.enabled, settings: { ...settings } };
}

export function validateSilenceTrim(form: SilenceTrimForm): string | null {
  if (!Number.isFinite(form.thresholdDb) || form.thresholdDb > 0)
    return 'Silence threshold must be 0 dB or lower.';
  if (!Number.isFinite(form.padMs) || form.padMs < 0)
    return 'Keep at each end must be zero or greater.';
  return null;
}

// ---- consonant soften -----------------------------------------------------------------------------

export type AdynEqDraft = Omit<AdynEqParams, 'highpassHz'>;
export type DeesserDraft = Omit<DeesserParams, 'highpassHz'>;

export interface ConsonantSoftenForm {
  enabled: boolean;
  engine: ConsonantSoftenEngine;
  preset: ConsonantSoftenPreset;
  /** The preset the custom drafts are seeded from when custom is (re)selected. */
  seedPreset: Exclude<ConsonantSoftenPreset, 'custom'>;
  adynEq: AdynEqDraft;
  deesser: DeesserDraft;
  highpassEnabled: boolean;
  highpassHz: number;
}

export const DEFAULT_HIGHPASS_HZ = 80;
export const MIN_HIGHPASS_HZ = 40;
export const MAX_HIGHPASS_HZ = 160;

/** The strong ladder rung doubles as the property defaults on the host's records. */
const STRONG_ADYNEQ: AdynEqDraft = {
  thresholdDb: -34,
  ratio: 6,
  rangeDb: 15,
  detectFrequencyHz: 6000,
  detectQ: 0.7,
  targetFrequencyHz: 6000,
  targetQ: 0.7,
  attackMs: 5,
  releaseMs: 60,
  shelfFrequencyHz: 6500,
  shelfGainDb: -3,
};

const STRONG_DEESSER: DeesserDraft = {
  intensity: 0.7,
  makeupAmount: 0.7,
  frequency: 0.5,
  shelfFrequencyHz: 6500,
  shelfGainDb: -3,
};

/** `ConsonantSoftenPresets.ResolveAdynEq`, per the locked spec tables. */
export const ADYNEQ_PRESETS: Record<Exclude<ConsonantSoftenPreset, 'custom'>, AdynEqDraft> = {
  light: { ...STRONG_ADYNEQ, thresholdDb: -20, ratio: 2, rangeDb: 6 },
  medium: { ...STRONG_ADYNEQ, thresholdDb: -26, ratio: 4, rangeDb: 12 },
  strong: STRONG_ADYNEQ,
};

/** `ConsonantSoftenPresets.ResolveDeesser`. */
export const DEESSER_PRESETS: Record<Exclude<ConsonantSoftenPreset, 'custom'>, DeesserDraft> = {
  light: { ...STRONG_DEESSER, intensity: 0.35, makeupAmount: 0.5 },
  medium: { ...STRONG_DEESSER, intensity: 0.5, makeupAmount: 0.5 },
  strong: STRONG_DEESSER,
};

function isLadderPreset(preset: string): preset is Exclude<ConsonantSoftenPreset, 'custom'> {
  return preset === 'light' || preset === 'medium' || preset === 'strong';
}

/** Unknown preset ids resolve to strong, as on the host. */
function seedOf(preset: string): Exclude<ConsonantSoftenPreset, 'custom'> {
  return isLadderPreset(preset) ? preset : 'strong';
}

function seeded(
  form: ConsonantSoftenForm,
  seed: Exclude<ConsonantSoftenPreset, 'custom'>,
): ConsonantSoftenForm {
  return {
    ...form,
    seedPreset: seed,
    adynEq: { ...ADYNEQ_PRESETS[seed] },
    deesser: { ...DEESSER_PRESETS[seed] },
    // Presets never carry a highpass, so the reset clears it too.
    highpassEnabled: false,
    highpassHz: DEFAULT_HIGHPASS_HZ,
  };
}

function adynEqDraft(p: AdynEqParams): AdynEqDraft {
  const draft: AdynEqParams = { ...p };
  delete draft.highpassHz;
  return draft;
}

function deesserDraft(p: DeesserParams): DeesserDraft {
  const draft: DeesserParams = { ...p };
  delete draft.highpassHz;
  return draft;
}

export function toConsonantSoftenForm(
  config: AudioPostProcessStepConfig | undefined,
): ConsonantSoftenForm {
  const settings = (config?.settings as ConsonantSoftenSettings | null) ?? {
    engine: 'adyneq',
    preset: 'strong',
  };
  const engine: ConsonantSoftenEngine = settings.engine === 'deesser' ? 'deesser' : 'adyneq';
  const preset: ConsonantSoftenPreset =
    settings.preset === 'custom' ? 'custom' : seedOf(settings.preset);

  let form = seeded(
    {
      enabled: config?.enabled ?? false,
      engine,
      preset,
      seedPreset: 'strong',
      adynEq: STRONG_ADYNEQ,
      deesser: STRONG_DEESSER,
      highpassEnabled: false,
      highpassHz: DEFAULT_HIGHPASS_HZ,
    },
    preset === 'custom' ? 'strong' : preset,
  );

  // Saved custom params win over the seed — they are the user's last save.
  if (settings.adynEq)
    form = { ...form, adynEq: { ...form.adynEq, ...adynEqDraft(settings.adynEq) } };
  if (settings.deesser)
    form = { ...form, deesser: { ...form.deesser, ...deesserDraft(settings.deesser) } };

  const highpass = settings.adynEq?.highpassHz ?? settings.deesser?.highpassHz;
  return {
    ...form,
    highpassEnabled: highpass != null,
    highpassHz: highpass ?? DEFAULT_HIGHPASS_HZ,
  };
}

/**
 * Selects a preset. Non-custom presets become the seed for later custom edits; either way the
 * drafts are re-seeded, so unsaved tweaks are discarded.
 */
export function setSoftenPreset(
  form: ConsonantSoftenForm,
  preset: ConsonantSoftenPreset,
): ConsonantSoftenForm {
  const seed = preset === 'custom' ? form.seedPreset : seedOf(preset);
  return { ...seeded(form, seed), preset };
}

export function setSoftenEngine(
  form: ConsonantSoftenForm,
  engine: ConsonantSoftenEngine,
): ConsonantSoftenForm {
  return { ...form, engine };
}

/** Key order follows the host's records, so both UIs write byte-identical settings JSON. */
function adynEqParams(d: AdynEqDraft, highpassHz: number | undefined): AdynEqParams {
  return {
    thresholdDb: d.thresholdDb,
    ratio: d.ratio,
    rangeDb: d.rangeDb,
    detectFrequencyHz: d.detectFrequencyHz,
    detectQ: d.detectQ,
    targetFrequencyHz: d.targetFrequencyHz,
    targetQ: d.targetQ,
    attackMs: d.attackMs,
    releaseMs: d.releaseMs,
    shelfFrequencyHz: d.shelfFrequencyHz,
    shelfGainDb: d.shelfGainDb,
    ...(highpassHz === undefined ? {} : { highpassHz }),
  };
}

function deesserParams(d: DeesserDraft, highpassHz: number | undefined): DeesserParams {
  return {
    intensity: d.intensity,
    makeupAmount: d.makeupAmount,
    frequency: d.frequency,
    shelfFrequencyHz: d.shelfFrequencyHz,
    shelfGainDb: d.shelfGainDb,
    ...(highpassHz === undefined ? {} : { highpassHz }),
  };
}

export function buildConsonantSoftenConfig(form: ConsonantSoftenForm): AudioPostProcessStepConfig {
  const custom = form.preset === 'custom';
  const hp = form.highpassEnabled ? form.highpassHz : undefined;
  const settings: ConsonantSoftenSettings = {
    engine: form.engine,
    preset: form.preset,
    ...(custom
      ? { adynEq: adynEqParams(form.adynEq, hp), deesser: deesserParams(form.deesser, hp) }
      : {}),
  };
  return {
    stepId: AUDIO_STEP_IDS.consonantSoften,
    enabled: form.enabled,
    settings: { ...settings },
  };
}

function finite(...values: number[]): boolean {
  return values.every((v) => Number.isFinite(v));
}

export function validateConsonantSoften(form: ConsonantSoftenForm): string | null {
  if (form.preset !== 'custom') return null;
  if (form.engine === 'adyneq') {
    const a = form.adynEq;
    if (!finite(...Object.values(a))) return 'Every adynEQ field needs a number.';
    if (a.ratio < 1) return 'Ratio must be 1 or greater.';
    if (a.attackMs < 0) return 'Attack must be zero or greater.';
    if (a.releaseMs < 0) return 'Release must be zero or greater.';
  } else {
    const d = form.deesser;
    if (!finite(...Object.values(d))) return 'Every deesser field needs a number.';
    if (d.intensity < 0 || d.intensity > 1) return 'Intensity must be between 0 and 1.';
    if (d.makeupAmount < 0 || d.makeupAmount > 1) return 'Makeup amount must be between 0 and 1.';
    if (d.frequency < 0 || d.frequency > 1) return 'Split frequency must be between 0 and 1.';
  }
  if (
    form.highpassEnabled &&
    (!Number.isFinite(form.highpassHz) ||
      form.highpassHz < MIN_HIGHPASS_HZ ||
      form.highpassHz > MAX_HIGHPASS_HZ)
  )
    return `Highpass cutoff must be between ${MIN_HIGHPASS_HZ} and ${MAX_HIGHPASS_HZ} Hz.`;
  return null;
}

// ---- scalar cards ---------------------------------------------------------------------------------

export function validateWer(value: number): string | null {
  return Number.isFinite(value) && value >= 0 && value <= 1
    ? null
    : 'WER threshold must be between 0 and 1.';
}

export function validateChunkPause(value: number): string | null {
  return Number.isFinite(value) && value >= 0 ? null : 'Pause must be zero or greater.';
}

export function validateAttempts(value: number): string | null {
  return Number.isInteger(value) && value >= 1
    ? null
    : 'Max audio attempts must be a whole number of 1 or more.';
}

const PAUSE_LABELS: Record<keyof PauseDurations, string> = {
  volumeMs: 'Volume pause',
  partMs: 'Part pause',
  chapterMs: 'Chapter pause',
  paragraphMs: 'Paragraph pause',
  pauseMs: 'Pause',
};

export function validatePauses(pauses: PauseDurations): string | null {
  for (const key of Object.keys(PAUSE_LABELS) as (keyof PauseDurations)[]) {
    const v = pauses[key];
    if (!Number.isFinite(v) || v < 0) return `${PAUSE_LABELS[key]} must be zero or greater.`;
  }
  return null;
}

/** Structural equality for the small plain-object forms on this page (draft vs saved). */
export function sameForm<T>(a: T, b: T): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
