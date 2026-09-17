import {
  PreviewStageDto,
  PreviewStepRequest,
  StepCatalogEntryDto,
  VoiceDto,
  voiceOriginalUrl,
  workspaceUrl,
} from '@app/api';
import { toFormField } from '@app/shared/settings-schema';
import { SettingsSchema, SettingsValues } from '@app/ui/settings-form/settings-form';

/** Which steps are ticked, by step id. */
export type Ticks = Record<string, boolean>;
/** Each step's current dial values, by step id. */
export type StepValues = Record<string, SettingsValues>;

/** A catalog entry as the settings form's schema. */
export function toStepSchema(entry: StepCatalogEntryDto): SettingsSchema {
  return { type: entry.stepId, fields: entry.dials.map((f) => toFormField(f)) };
}

/** Every step's dials at their catalog defaults. */
export function seedValues(catalog: StepCatalogEntryDto[]): StepValues {
  return Object.fromEntries(catalog.map((e) => [e.stepId, { ...e.defaults }]));
}

/**
 * The chain to render: the ticked steps in catalog order (the host orders them anyway; sending them
 * ordered keeps the request comparable render to render) with their current dials.
 */
export function buildPreviewSteps(
  catalog: StepCatalogEntryDto[],
  ticks: Ticks,
  values: StepValues,
): PreviewStepRequest[] {
  return catalog
    .filter((e) => ticks[e.stepId])
    .map((e) => ({ stepId: e.stepId, settings: { ...(values[e.stepId] ?? e.defaults) } }));
}

/**
 * Whether the current tick/dial state is exactly what the last render was asked for. Anything else
 * is a stale render: Apply must never write bytes the user has not heard.
 */
export function sameRender(
  rendered: PreviewStepRequest[] | null,
  current: PreviewStepRequest[],
): boolean {
  return rendered !== null && JSON.stringify(rendered) === JSON.stringify(current);
}

export interface ApplyGate {
  anyTicked: boolean;
  hasRender: boolean;
  stale: boolean;
  busy: boolean;
}

/** Why Apply is disabled, for its tooltip; null when it may run. */
export function applyBlockedReason(gate: ApplyGate): string | null {
  if (gate.busy) return 'Wait for the current render or write to finish.';
  if (!gate.anyTicked) return 'Tick at least one step.';
  if (!gate.hasRender) return 'Preview first — you apply what you heard.';
  if (gate.stale) return 'Ticks or dials changed since the last render — preview again.';
  return null;
}

/** Hiss reduce on top of denoise mostly repeats work the denoiser already did. */
export function hissRedundant(ticks: Ticks): boolean {
  return !!ticks['denoise'] && !!ticks['hiss-reduce'];
}

export function stageFor(stages: PreviewStageDto[] | null, stepId: string): PreviewStageDto | null {
  return stages?.find((s) => s.stepId === stepId) ?? null;
}

/**
 * What the "Original" player plays: the stored original once the voice is edited, else the live
 * WAV — which is the original until the first apply. Null when the voice has no audio. No
 * cache-buster here: the player appends one from its `cacheKey`.
 */
export function originalAudioUrl(folder: string, voice: VoiceDto): string | null {
  if (!voice.audioFileName) return null;
  return voice.isEdited
    ? voiceOriginalUrl(folder, voice.id)
    : workspaceUrl(folder, voice.audioFileName);
}
