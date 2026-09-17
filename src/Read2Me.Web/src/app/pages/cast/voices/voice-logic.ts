import { CharacterVoicesDto, Guid, VoiceDto, VoiceSource, workspaceUrl } from '@app/api';
import { VoiceBatchMessage } from '@app/live/live-messages';
import { SettingsValues } from '@app/ui/settings-form/settings-form';

/**
 * The voice cards' decision rules (ticket 16), kept out of the components so they are unit-testable:
 * when a source switch needs confirming, how a settings override round-trips its JSON column, how a
 * batch's `voiceUpdated` lands on the list, and what the prompt-batch scope dialog answers.
 */

// ---- source toggle -----------------------------------------------------------------------------

/**
 * What `SetVoiceSource` discards (`SetVoiceSourceMutationImplementation`): switching to Prompt drops
 * a recording, switching to Reference drops the design prompt. The message to confirm, or null when
 * nothing is lost (a bare switch needs no dialog).
 */
export function sourceSwitchWarning(voice: VoiceDto, target: VoiceSource): string | null {
  if (voice.source === target) return null;
  if (target === 'Generated' && voice.audioFileName) {
    return 'Switching to Prompt drops the uploaded recording. The voice will need generated audio.';
  }
  if (target === 'Uploaded' && voice.designPrompt) {
    return 'Switching to Reference drops the design prompt. The voice will need a recording.';
  }
  return null;
}

/** Fresh audio discards a voice-editor edit — allowed, but never silently (Blazor parity). */
export const EDITED_OVERWRITE_MESSAGE =
  "This voice's audio has been edited. Regenerating replaces it.";

export function overwriteEditWarning(voice: VoiceDto): string | null {
  return voice.isEdited ? EDITED_OVERWRITE_MESSAGE : null;
}

/** Generate audio needs a non-blank design prompt (the draft counts, as in Blazor). */
export function canGenerateAudio(promptDraft: string | null | undefined): boolean {
  return (promptDraft ?? '').trim().length > 0;
}

// ---- settings overrides --------------------------------------------------------------------------

/** The stored override column as form values: `{}` for null, blank or anything but a JSON object. */
export function overrideValues(json: string | null | undefined): SettingsValues {
  if (!json || json.trim() === '') return {};
  try {
    const parsed: unknown = JSON.parse(json);
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)
      ? { ...(parsed as SettingsValues) }
      : {};
  } catch {
    return {};
  }
}

/** The form's sparse patch as the column value: null clears the override, else compact JSON. */
export function overrideJson(patch: SettingsValues): string | null {
  return Object.keys(patch).length === 0 ? null : JSON.stringify(patch);
}

/** True when saving would change the stored column. */
export function overrideDirty(stored: string | null | undefined, patch: SettingsValues): boolean {
  return JSON.stringify(overrideValues(stored)) !== JSON.stringify(patch);
}

// ---- batch --------------------------------------------------------------------------------------

/** What the prompt-batch scope dialog answers (research §4). */
export type PromptBatchScope = 'only-without' | 'regenerate-all';

/**
 * What the toolbar does with the scope dialog's answer (research §4): without any voices the
 * batch starts straight away; otherwise a cancelled dialog (null) starts nothing, and
 * "regenerate all" replans every character after deleting their voices, which is destructive
 * and gets its own confirm.
 */
export interface PromptBatchRequest {
  regenerateAll: boolean;
  /** The destructive confirm the toolbar must pass before starting, or null. */
  confirm: string | null;
}

export const REGENERATE_ALL_MESSAGE =
  "Every character's existing voices, their audio and their voice rules are deleted before " +
  'replanning. This cannot be undone.';

export function promptBatchRequest(
  anyVoices: boolean,
  scope: PromptBatchScope | null,
): PromptBatchRequest | null {
  if (!anyVoices) return { regenerateAll: false, confirm: null };
  if (scope === null) return null;
  return scope === 'regenerate-all'
    ? { regenerateAll: true, confirm: REGENERATE_ALL_MESSAGE }
    : { regenerateAll: false, confirm: null };
}

/**
 * Lands a `voiceUpdated` message on the list: only the fields the batch reports change. Returns the
 * same object when the voice is not in the list (a prompt batch just created it — reload instead).
 */
export function applyVoiceUpdated(
  voices: CharacterVoicesDto,
  m: VoiceBatchMessage,
): CharacterVoicesDto {
  const index = voices.voices.findIndex((v) => v.id === m.voiceId);
  if (index < 0) return voices;
  const current = voices.voices[index]!;
  const next: VoiceDto = {
    ...current,
    designPrompt: m.designPrompt ?? current.designPrompt,
    audioFileName: m.audioFileName ?? current.audioFileName,
    transcript: m.transcript ?? current.transcript,
    // Batch-generated audio is fresh: any earlier edit is gone with it.
    isEdited: m.audioFileName ? false : current.isEdited,
  };
  const list = voices.voices.slice();
  list[index] = next;
  return { ...voices, voices: list };
}

// ---- audio ----------------------------------------------------------------------------------------

/** The player's source for a voice's reference audio, cache-busted by the tab's version counter. */
export function voiceAudioUrl(
  folder: string,
  voice: VoiceDto,
  versions: Record<Guid, number>,
): string | null {
  return voice.audioFileName
    ? workspaceUrl(folder, voice.audioFileName, versions[voice.id] ?? 0)
    : null;
}
