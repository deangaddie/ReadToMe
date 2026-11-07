import type {
  AssemblyMessage,
  AudioGenMessage,
  BookEditMessage,
  HistoryMessage,
  ItemStatusEntry,
  LlmMessage,
  LlmTestMessage,
  ParagraphStatusEntry,
  PreflightMessage,
  ServiceStatusMessage,
  ThroughputSnapshot,
  VoiceBatchMessage,
  WatchdogMessage,
} from '@app/live/live-messages';

/**
 * Hub messages for specs. The hub sends every field, null when unset (#238), so a spec names the
 * fields its case is about and these fill the rest with null, as the wire would.
 */

export const llmMessage = (m: Pick<LlmMessage, 'kind'> & Partial<LlmMessage>): LlmMessage => ({
  thinking: null,
  content: null,
  paragraphPreview: null,
  prompt: null,
  configId: null,
  configName: null,
  tokensIn: null,
  tokensOut: null,
  generationMs: null,
  tokensPerSecond: null,
  reason: null,
  step: null,
  itemCount: null,
  ...m,
});

export const audioGenMessage = (
  m: Pick<AudioGenMessage, 'kind'> & Partial<AudioGenMessage>,
): AudioGenMessage => ({
  id: '',
  attempt: 0,
  character: null,
  text: null,
  ok: null,
  reason: null,
  stepId: null,
  applied: null,
  transcript: null,
  wer: null,
  rescued: null,
  model: null,
  ...m,
});

export const assemblyMessage = (
  m: Pick<AssemblyMessage, 'kind'> & Partial<AssemblyMessage>,
): AssemblyMessage => ({
  phase: null,
  fraction: null,
  reason: null,
  folder: null,
  outputFileName: null,
  ...m,
});

export const voiceBatchMessage = (
  m: Pick<VoiceBatchMessage, 'kind'> & Partial<VoiceBatchMessage>,
): VoiceBatchMessage => ({
  operation: null,
  processed: null,
  total: null,
  failed: null,
  currentVoiceName: null,
  characterId: null,
  voiceId: null,
  designPrompt: null,
  audioFileName: null,
  transcript: null,
  referenceSeconds: null,
  referenceWarning: null,
  ...m,
});

export const watchdogMessage = (
  m: Pick<WatchdogMessage, 'kind' | 'service'> & Partial<WatchdogMessage>,
): WatchdogMessage => ({ reason: null, ...m });

export const serviceStatusMessage = (
  m: Pick<ServiceStatusMessage, 'name' | 'status'> & Partial<ServiceStatusMessage>,
): ServiceStatusMessage => ({ op: null, ok: null, error: null, ...m });

export const preflightMessage = (
  m: Pick<PreflightMessage, 'kind' | 'run'> & Partial<PreflightMessage>,
): PreflightMessage => ({
  name: null,
  stage: null,
  error: null,
  ok: null,
  reason: null,
  ...m,
});

export const bookEditMessage = (
  m: Pick<BookEditMessage, 'kind' | 'program'> & Partial<BookEditMessage>,
): BookEditMessage => ({
  done: null,
  total: null,
  cancelled: null,
  rows: null,
  reason: null,
  ...m,
});

export const llmTestMessage = (
  m: Pick<LlmTestMessage, 'kind' | 'configId'> & Partial<LlmTestMessage>,
): LlmTestMessage => ({ reason: null, ...m });

export const historyMessage = (
  m: Pick<HistoryMessage, 'kind' | 'folder'> & Partial<HistoryMessage>,
): HistoryMessage => ({ step: null, reason: null, phase: null, ...m });

export const paragraphEntry = (m: Partial<ParagraphStatusEntry>): ParagraphStatusEntry => ({
  status: null,
  outcome: null,
  ...m,
});

export const itemEntry = (m: Partial<ItemStatusEntry>): ItemStatusEntry => ({
  status: null,
  outcome: null,
  audioVersion: null,
  review: null,
  ...m,
});

type ConfigThroughput = ThroughputSnapshot['perConfig'][number];

export const configThroughput = (
  m: Pick<ConfigThroughput, 'configId' | 'configName' | 'requests'> & Partial<ConfigThroughput>,
): ConfigThroughput => ({ tokensOut: null, generationMs: null, tokensPerSecond: null, ...m });
