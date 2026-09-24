import { AttributionPromptStyle, LlmApiType, LlmServerConfig } from '@app/api';
import { isAbsoluteUrl } from '@app/shared/config-form';

/**
 * Edit-state for an `LlmServerConfig`, mirroring Blazor's `LlmServerConfigForm`: numeric fields are
 * held as text so a blank one means "omit" (the server default applies).
 */
export interface LlmConfigForm {
  name: string;
  apiType: LlmApiType;
  baseUrl: string;
  apiKey: string;
  model: string;
  temperature: string;
  topP: string;
  maxTokens: string;
  frequencyPenalty: string;
  presencePenalty: string;
  /** Paragraphs per attribution request; blank means 1. */
  attributionBatchSize: string;
  promptStyle: AttributionPromptStyle;
  supportsModelSwitch: boolean;
}

export const API_TYPE_LABELS: Record<LlmApiType, string> = {
  [LlmApiType.OpenAiCompatible]: 'OpenAI compatible',
};

export const EMPTY_LLM_FORM: LlmConfigForm = {
  name: '',
  apiType: LlmApiType.OpenAiCompatible,
  baseUrl: '',
  apiKey: '',
  model: '',
  temperature: '',
  topP: '',
  maxTokens: '',
  frequencyPenalty: '',
  presencePenalty: '',
  attributionBatchSize: '1',
  promptStyle: AttributionPromptStyle.Full,
  supportsModelSwitch: false,
};

const text = (value: number | null): string => (value === null ? '' : String(value));

export function toLlmForm(config: LlmServerConfig): LlmConfigForm {
  return {
    name: config.name,
    apiType: config.apiType,
    baseUrl: config.baseUrl,
    apiKey: config.apiKey ?? '',
    model: config.model ?? '',
    temperature: text(config.temperature),
    topP: text(config.topP),
    maxTokens: text(config.maxTokens),
    frequencyPenalty: text(config.frequencyPenalty),
    presencePenalty: text(config.presencePenalty),
    attributionBatchSize: text(config.attributionBatchSize),
    promptStyle: config.promptStyle,
    supportsModelSwitch: config.supportsModelSwitch,
  };
}

const NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/;
const WHOLE = /^[+-]?\d+$/;
const INT32_MAX = 2147483647;

/** Blank parses to null; `undefined` means the text is not a number of that kind. */
function parse(value: string, pattern: RegExp): number | null | undefined {
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (!pattern.test(trimmed)) return undefined;
  const parsed = Number(trimmed);
  // The host binds whole numbers to int32, as Blazor's int.TryParse did.
  return pattern === WHOLE && Math.abs(parsed) > INT32_MAX ? undefined : parsed;
}

/** The first problem, worded and ordered as Blazor's `LlmServerConfigForm.Validate`; null when valid. */
export function validateLlmForm(form: LlmConfigForm): string | null {
  if (!form.name.trim()) return 'Name is required.';
  if (!form.baseUrl.trim()) return 'Base URL is required.';
  if (!isAbsoluteUrl(form.baseUrl))
    return 'Base URL must be a valid absolute URL (e.g. http://localhost:8080).';

  if (parse(form.temperature, NUMBER) === undefined) return 'Temperature must be a number.';
  if (parse(form.topP, NUMBER) === undefined) return 'Top P must be a number.';
  if (parse(form.maxTokens, WHOLE) === undefined) return 'Max tokens must be a whole number.';
  if (parse(form.frequencyPenalty, NUMBER) === undefined)
    return 'Frequency penalty must be a number.';
  if (parse(form.presencePenalty, NUMBER) === undefined)
    return 'Presence penalty must be a number.';
  const batch = parse(form.attributionBatchSize, WHOLE);
  if (batch === undefined || (batch !== null && batch < 1))
    return 'Paragraphs per request must be a whole number of 1 or more.';

  return null;
}

/** The config a valid form describes. `id` 0 for one not yet saved. */
export function buildLlmConfig(form: LlmConfigForm, id: number): LlmServerConfig {
  const num = (value: string, pattern: RegExp) => parse(value, pattern) ?? null;
  return {
    id,
    name: form.name.trim(),
    apiType: form.apiType,
    baseUrl: form.baseUrl.trim(),
    apiKey: form.apiKey.trim() || null,
    model: form.model.trim() || null,
    temperature: num(form.temperature, NUMBER),
    topP: num(form.topP, NUMBER),
    maxTokens: num(form.maxTokens, WHOLE),
    frequencyPenalty: num(form.frequencyPenalty, NUMBER),
    presencePenalty: num(form.presencePenalty, NUMBER),
    attributionBatchSize: num(form.attributionBatchSize, WHOLE) ?? 1,
    promptStyle: form.promptStyle,
    supportsModelSwitch: form.supportsModelSwitch,
  };
}

export function sameLlmForm(a: LlmConfigForm, b: LlmConfigForm): boolean {
  return (Object.keys(a) as (keyof LlmConfigForm)[]).every((key) => a[key] === b[key]);
}
