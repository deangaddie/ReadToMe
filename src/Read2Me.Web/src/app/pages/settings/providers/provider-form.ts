import { TextSubstitutionStep, ToSentenceCaseConfig } from '@app/api';
import { duplicateName, isAbsoluteUrl } from '@app/shared/config-form';
import {
  SettingsField,
  SettingsValues,
  effectiveValue,
  isFieldValid,
} from '@app/ui/settings-form/settings-form';

/**
 * What the four provider config entities share: identity in properties, everything else in
 * `settingsJson`. Only a TTS config carries the three text-processing properties.
 */
export interface ProviderConfig {
  id: number;
  name: string;
  type: number;
  settingsJson: string;
  enabledStepIds?: string[];
  substitutionSteps?: TextSubstitutionStep[];
  toSentenceCaseConfig?: ToSentenceCaseConfig | null;
}

export const TO_SENTENCE_CASE_STEP = 'to-sentence-case';

export interface SubstitutionRow {
  id: string;
  fromText: string;
  toText: string;
}

export interface ToSentenceCaseForm {
  paragraphEnabled: boolean;
  wordEnabled: boolean;
  /** Text, so a half-typed number is a validation message rather than a lost keystroke. */
  wordMinLength: string;
}

/**
 * A TTS config's text processing, shaped as Blazor's `ParagraphTtsServiceConfigForm`: one ordered
 * list of enabled step ids — built-ins and substitutions alike, the order the steps run in.
 */
export interface TextProcessingForm {
  enabledStepIds: string[];
  substitutions: SubstitutionRow[];
  toSentenceCase: ToSentenceCaseForm;
}

/** Edit-state for a provider config of any of the four areas. */
export interface ProviderForm {
  name: string;
  type: number;
  baseUrl: string;
  /**
   * `settingsJson` minus the base URL, keys lower-cased: the stored case differs per provider
   * record (`cfg_value`, `maxChunkChars`, `PassThreshold`) and the host reads any of them.
   */
  settings: SettingsValues;
  /** Null outside the TTS area. */
  text: TextProcessingForm | null;
}

// New-config defaults of Blazor's `ToSentenceCaseFormItem`.
const DEFAULT_TO_SENTENCE_CASE: ToSentenceCaseForm = {
  paragraphEnabled: true,
  wordEnabled: true,
  wordMinLength: '5',
};

const BASE_URL_KEY = 'baseurl';
const WHOLE = /^\d+$/;

export function emptyProviderForm(type: number, withText: boolean): ProviderForm {
  return {
    name: '',
    type,
    baseUrl: '',
    settings: {},
    text: withText
      ? { enabledStepIds: [], substitutions: [], toSentenceCase: DEFAULT_TO_SENTENCE_CASE }
      : null,
  };
}

function parseSettings(json: string): SettingsValues {
  try {
    const parsed: unknown = JSON.parse(json);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
    return Object.fromEntries(Object.entries(parsed).map(([k, v]) => [k.toLowerCase(), v]));
  } catch {
    return {};
  }
}

export function toProviderForm(config: ProviderConfig, withText: boolean): ProviderForm {
  const { [BASE_URL_KEY]: baseUrl, ...settings } = parseSettings(config.settingsJson);
  const tsc = config.toSentenceCaseConfig;
  return {
    name: config.name,
    type: config.type,
    baseUrl: typeof baseUrl === 'string' ? baseUrl : '',
    settings,
    text: withText
      ? {
          enabledStepIds: [...(config.enabledStepIds ?? [])],
          substitutions: [...(config.substitutionSteps ?? [])]
            .sort((a, b) => a.order - b.order)
            .map((s) => ({ id: s.id, fromText: s.fromText, toText: s.toText })),
          toSentenceCase: tsc
            ? {
                paragraphEnabled: tsc.paragraphEnabled,
                wordEnabled: tsc.wordEnabled,
                wordMinLength: String(tsc.wordMinLength),
              }
            : DEFAULT_TO_SENTENCE_CASE,
        }
      : null,
  };
}

// ---- schema fields ⇄ settings -------------------------------------------------------------------

/** The stored values of `fields`, keyed as the settings form wants them; absent keys take the default. */
export function fieldValues(fields: readonly SettingsField[], settings: SettingsValues): SettingsValues {
  const values: SettingsValues = {};
  for (const field of fields) {
    const key = field.key.toLowerCase();
    if (Object.prototype.hasOwnProperty.call(settings, key)) values[field.key] = settings[key];
  }
  return values;
}

/** What the settings form emitted, folded back in; keys it does not show right now are kept. */
export function mergeFieldValues(settings: SettingsValues, values: SettingsValues): SettingsValues {
  const merged = { ...settings };
  for (const [key, value] of Object.entries(values)) merged[key.toLowerCase()] = value;
  return merged;
}

// ---- validation ---------------------------------------------------------------------------------

function fieldProblem(field: SettingsField, value: unknown): string | null {
  if (field.kind !== 'number') return null;
  if (!isFieldValid(field, value)) {
    if (field.min !== undefined && field.max !== undefined)
      return `${field.label} must be between ${field.min} and ${field.max}.`;
    if (field.min !== undefined) return `${field.label} must be ${field.min} or more.`;
    return `${field.label} must be a number.`;
  }
  // The host binds a step-1 field to an int.
  if (field.step === 1 && value != null && !Number.isInteger(value))
    return `${field.label} must be a whole number.`;
  return null;
}

/**
 * The first problem, or null when the form may be saved. Name and base URL are worded and ordered
 * as Blazor's config forms; `urlExample` is the provider type's placeholder.
 */
export function validateProviderForm(
  form: ProviderForm,
  fields: readonly SettingsField[],
  urlExample: string,
): string | null {
  if (!form.name.trim()) return 'Name is required.';
  if (!form.baseUrl.trim()) return 'Base URL is required.';
  if (!isAbsoluteUrl(form.baseUrl))
    return `Base URL must be a valid absolute URL (e.g. ${urlExample}).`;

  const values = fieldValues(fields, form.settings);
  for (const field of fields) {
    const problem = fieldProblem(field, effectiveValue(field, values));
    if (problem) return problem;
  }

  const text = form.text;
  if (text?.enabledStepIds.includes(TO_SENTENCE_CASE_STEP) && text.toSentenceCase.wordEnabled) {
    const length = text.toSentenceCase.wordMinLength.trim();
    if (!WHOLE.test(length) || Number(length) < 1)
      return 'Minimum word length must be a whole number of 1 or more.';
  }
  return null;
}

// ---- build --------------------------------------------------------------------------------------

function storedValue(field: SettingsField, value: unknown): unknown {
  if (field.kind !== 'string' && field.kind !== 'text') return value;
  const trimmed = typeof value === 'string' ? value.trim() : '';
  // An optional string (API key, model) the provider record holds as null when unset.
  return trimmed || (field.default == null ? null : '');
}

/**
 * The config a valid form describes; `id` 0 for one not yet saved. The host rewrites `settingsJson`
 * into the provider record's own key case and order, so only the values matter here.
 */
export function buildProviderConfig(
  form: ProviderForm,
  id: number,
  fields: readonly SettingsField[],
): ProviderConfig {
  const values = fieldValues(fields, form.settings);
  const edited = new Set(fields.map((f) => f.key.toLowerCase()));
  // A stored key no field edits (Qwen3-Base's API key, set over the API) rides along untouched.
  const settings: SettingsValues = Object.fromEntries(
    Object.entries(form.settings).filter(([key]) => !edited.has(key)),
  );
  settings['baseUrl'] = form.baseUrl.trim();
  for (const field of fields) settings[field.key] = storedValue(field, effectiveValue(field, values));

  const config: ProviderConfig = {
    id,
    name: form.name.trim(),
    type: form.type,
    settingsJson: JSON.stringify(settings),
  };
  const text = form.text;
  if (!text) return config;

  const tsc = text.toSentenceCase;
  return {
    ...config,
    enabledStepIds: [...text.enabledStepIds],
    substitutionSteps: text.substitutions.map((s, order) => ({
      id: s.id,
      paragraphTtsServiceConfigId: id,
      fromText: s.fromText,
      toText: s.toText,
      order,
    })),
    toSentenceCaseConfig: text.enabledStepIds.includes(TO_SENTENCE_CASE_STEP)
      ? {
          // The host keeps one row per config and matches it by config, never by this id.
          id: 0,
          paragraphTtsServiceConfigId: id,
          paragraphEnabled: tsc.paragraphEnabled,
          wordEnabled: tsc.wordEnabled,
          wordMinLength: Number(tsc.wordMinLength.trim()) || 1,
        }
      : null,
  };
}

// ---- text processing ----------------------------------------------------------------------------

export function setStepEnabled(
  text: TextProcessingForm,
  stepId: string,
  enabled: boolean,
): TextProcessingForm {
  const without = text.enabledStepIds.filter((id) => id !== stepId);
  if (!enabled) return { ...text, enabledStepIds: without };
  return text.enabledStepIds.includes(stepId)
    ? text
    : { ...text, enabledStepIds: [...text.enabledStepIds, stepId] };
}

/** A new row starts enabled so it takes effect as soon as it is saved. */
export function addSubstitution(text: TextProcessingForm, id: string): TextProcessingForm {
  return {
    ...text,
    substitutions: [...text.substitutions, { id, fromText: '', toText: '' }],
    enabledStepIds: [...text.enabledStepIds, id],
  };
}

export function updateSubstitution(
  text: TextProcessingForm,
  id: string,
  change: Partial<Omit<SubstitutionRow, 'id'>>,
): TextProcessingForm {
  return {
    ...text,
    substitutions: text.substitutions.map((s) => (s.id === id ? { ...s, ...change } : s)),
  };
}

export function removeSubstitution(text: TextProcessingForm, id: string): TextProcessingForm {
  return {
    ...text,
    substitutions: text.substitutions.filter((s) => s.id !== id),
    enabledStepIds: text.enabledStepIds.filter((stepId) => stepId !== id),
  };
}

// ---- compare / duplicate ------------------------------------------------------------------------

function stable(value: unknown): unknown {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return value;
  const entries = Object.entries(value).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  return Object.fromEntries(entries.map(([k, v]) => [k, stable(v)]));
}

export function sameProviderForm(a: ProviderForm, b: ProviderForm): boolean {
  return (
    JSON.stringify({ ...a, settings: stable(a.settings) }) ===
    JSON.stringify({ ...b, settings: stable(b.settings) })
  );
}

/**
 * An unsaved copy: a free name, and new ids for its substitutions — a substitution's id is its
 * row's key, so the copy cannot share them with the original.
 */
export function duplicateProviderForm(
  form: ProviderForm,
  existingNames: readonly string[],
  newId: () => string,
): ProviderForm {
  const copy = { ...form, name: duplicateName(form.name, existingNames) };
  if (!form.text) return copy;
  const ids = new Map(form.text.substitutions.map((s) => [s.id, newId()]));
  return {
    ...copy,
    text: {
      ...form.text,
      substitutions: form.text.substitutions.map((s) => ({ ...s, id: ids.get(s.id) ?? s.id })),
      enabledStepIds: form.text.enabledStepIds.map((id) => ids.get(id) ?? id),
    },
  };
}
