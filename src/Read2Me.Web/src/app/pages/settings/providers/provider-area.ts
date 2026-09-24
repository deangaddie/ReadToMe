import { Type } from '@angular/core';
import {
  ParagraphTtsServiceType,
  ParagraphTtsSettingsApi,
  ProviderSettingsSchema,
  SemanticSimilarityServiceType,
  SemanticSimilaritySettingsApi,
  TranscriptionServiceType,
  TranscriptionSettingsApi,
  VoiceDesignServiceType,
  VoiceDesignSettingsApi,
} from '@app/api';
import { SettingsField, SettingsValues } from '@app/ui/settings-form/settings-form';
import { ProviderConfig, ProviderForm } from './provider-form';

export type ProviderAreaKey =
  'paragraph-tts' | 'voice-design' | 'transcription' | 'semantic-similarity';

/** The part of an area's `SettingsApi` the shared page, store and editor use. */
export interface ProviderAreaApi {
  list(): Promise<ProviderConfig[]>;
  create(config: Omit<ProviderConfig, 'id'>): Promise<ProviderConfig>;
  update(config: ProviderConfig): Promise<ProviderConfig>;
  delete(id: number): Promise<void>;
  active(): Promise<ProviderConfig | null>;
  setActive(id: number): Promise<void>;
  schema(type: number): Promise<ProviderSettingsSchema>;
}

export interface ProviderTypeOption {
  value: number;
  label: string;
  /** The base URL placeholder, and the example in its validation message. */
  urlExample: string;
}

/**
 * What differs between the four provider settings pages (ticket 22); the page, store and editor
 * are otherwise one. `key` is both the `/api/settings/{area}` segment and the hub's
 * `settingsChanged` area.
 */
export interface ProviderArea {
  key: ProviderAreaKey;
  title: string;
  subtitle: string;
  icon: string;
  /** "No {noun} configurations yet". */
  noun: string;
  api: Type<ProviderAreaApi>;
  types: ProviderTypeOption[];
  /** Only a TTS config has text-processing steps and substitutions. */
  hasTextProcessing: boolean;
  /**
   * Fields the provider schema leaves out because they are per-config, never per-voice (chunking,
   * carrier prefix, credentials); edited ahead of the schema's tuning fields.
   */
  configFields(type: number, settings: SettingsValues): SettingsField[];
  /** Lower-cased keys of the `configFields` every type of the area shares: a Type change keeps them. */
  perConfigKeys: readonly string[];
  /** A problem the field ranges cannot express; null when there is none. */
  validate?(form: ProviderForm): string | null;
  /** Extra list-row detail after the type and base URL. */
  detail?(form: ProviderForm): string | null;
}

const MAX_CHUNK_CHARS: SettingsField = {
  key: 'maxChunkChars',
  label: 'Max chunk chars',
  kind: 'number',
  min: 1,
  step: 1,
  default: 500,
  help: 'Soft cap on characters per TTS chunk. Larger = more coherent but heavier for the provider.',
};
const CARRIER_PREFIX: SettingsField = {
  key: 'carrierPrefixEnabled',
  label: 'Carrier prefix for short text',
  kind: 'boolean',
  default: false,
};
const CARRIER_MAX_TARGET_CHARS: SettingsField = {
  key: 'carrierMaxTargetChars',
  label: 'Carrier max target chars',
  kind: 'number',
  min: 1,
  step: 1,
  default: 30,
  help: "Text at or below this length gets the voice's reference transcript prepended, then trimmed off the audio.",
};

const QWEN3_CONNECTION: SettingsField[] = [
  { key: 'apiKey', label: 'API key (optional)', kind: 'string', default: null, secret: true },
  {
    key: 'model',
    label: 'Model (optional)',
    kind: 'string',
    default: null,
    help: 'Model id sent on the request.',
  },
];

const PASS_THRESHOLD_KEY = 'passthreshold';

export const PARAGRAPH_TTS_AREA: ProviderArea = {
  key: 'paragraph-tts',
  title: 'Paragraph TTS',
  subtitle: 'Servers that speak each paragraph item in its voice.',
  icon: 'record_voice_over',
  noun: 'TTS',
  api: ParagraphTtsSettingsApi,
  types: [
    { value: ParagraphTtsServiceType.VoxCpm2, label: 'VoxCpm2', urlExample: 'http://localhost:8003' },
    {
      value: ParagraphTtsServiceType.Chatterbox,
      label: 'Chatterbox',
      urlExample: 'http://localhost:8000',
    },
    {
      value: ParagraphTtsServiceType.ChatterboxTurbo,
      label: 'ChatterboxTurbo',
      urlExample: 'http://localhost:8001',
    },
    {
      value: ParagraphTtsServiceType.Qwen3Base,
      label: 'Qwen3Base',
      urlExample: 'http://localhost:8101',
    },
  ],
  hasTextProcessing: true,
  perConfigKeys: [MAX_CHUNK_CHARS, CARRIER_PREFIX, CARRIER_MAX_TARGET_CHARS].map((f) =>
    f.key.toLowerCase(),
  ),
  configFields: (_type, settings) =>
    settings[CARRIER_PREFIX.key.toLowerCase()] === true
      ? [MAX_CHUNK_CHARS, CARRIER_PREFIX, CARRIER_MAX_TARGET_CHARS]
      : [MAX_CHUNK_CHARS, CARRIER_PREFIX],
};

export const VOICE_DESIGN_AREA: ProviderArea = {
  key: 'voice-design',
  title: 'Voice design',
  subtitle: 'Servers that design a voice from a text description.',
  icon: 'graphic_eq',
  noun: 'voice design',
  api: VoiceDesignSettingsApi,
  types: [
    { value: VoiceDesignServiceType.VoxCpm2, label: 'VoxCpm2', urlExample: 'http://localhost:8003' },
    { value: VoiceDesignServiceType.Qwen3, label: 'Qwen3', urlExample: 'http://localhost:8100' },
  ],
  hasTextProcessing: false,
  perConfigKeys: [],
  configFields: (type) => (type === VoiceDesignServiceType.Qwen3 ? QWEN3_CONNECTION : []),
};

export const TRANSCRIPTION_AREA: ProviderArea = {
  key: 'transcription',
  title: 'Transcription',
  subtitle: 'Servers that transcribe generated audio for the accuracy check.',
  icon: 'hearing',
  noun: 'transcription',
  api: TranscriptionSettingsApi,
  types: [
    {
      value: TranscriptionServiceType.LocalWhisper,
      label: 'LocalWhisper',
      urlExample: 'http://localhost:9000',
    },
  ],
  hasTextProcessing: false,
  perConfigKeys: [],
  configFields: () => [],
};

export const SIMILARITY_AREA: ProviderArea = {
  key: 'semantic-similarity',
  title: 'Similarity',
  subtitle: 'Servers that score a transcript against its text when the words differ.',
  icon: 'compare_arrows',
  noun: 'similarity',
  api: SemanticSimilaritySettingsApi,
  types: [
    {
      value: SemanticSimilarityServiceType.MiniLmL6,
      label: 'MiniLmL6',
      urlExample: 'http://localhost:8200',
    },
    {
      value: SemanticSimilarityServiceType.MpnetBaseV2,
      label: 'MpnetBaseV2',
      urlExample: 'http://localhost:8201',
    },
  ],
  hasTextProcessing: false,
  perConfigKeys: [],
  configFields: () => [],
  // The schema's 0–1 range is inclusive; a threshold of exactly 0 or 1 passes everything or nothing.
  validate: (form) => {
    const threshold = form.settings[PASS_THRESHOLD_KEY];
    return threshold === 0 || threshold === 1
      ? 'Pass threshold must be between 0 and 1 (exclusive).'
      : null;
  },
  detail: (form) => {
    const threshold = form.settings[PASS_THRESHOLD_KEY];
    return typeof threshold === 'number' ? `threshold ${threshold.toFixed(2)}` : null;
  },
};

export function providerType(area: ProviderArea, type: number): ProviderTypeOption {
  return area.types.find((t) => t.value === type) ?? area.types[0]!;
}

export const PROVIDER_AREAS: Record<ProviderAreaKey, ProviderArea> = {
  'paragraph-tts': PARAGRAPH_TTS_AREA,
  'voice-design': VOICE_DESIGN_AREA,
  transcription: TRANSCRIPTION_AREA,
  'semantic-similarity': SIMILARITY_AREA,
};
