import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  CharacterVoicesDto,
  GenerateDesignPromptRequest,
  GenerateDesignPromptResponse,
  GenerateVoiceAudioRequest,
  GenerateVoiceAudioResponse,
  Guid,
  RenderedDesignPromptResponse,
  StartedResponse,
  TranscribeVoiceResponse,
  VoiceBatchStatusDto,
  VoiceDto,
} from './dtos';
import { projectUrl } from './projects-api';

/** The host's upload cap for voice reference audio (`VoiceEndpoints.MaxAudioBytes`). */
export const MAX_VOICE_AUDIO_BYTES = 200 * 1024 * 1024;

/**
 * `VoiceEndpoints.cs`: per-character voice reads, one voice by id, audio upload and transcription,
 * the design-prompt render / generate pair, single-voice synthesis and the two voice batches.
 * Voice edits (rename, prompt, overrides, source, delete…) go through `BookApi.execute`.
 */
export class VoicesApi {
  private readonly api = use(ApiClient);

  list(folder: string, characterId: Guid): Promise<CharacterVoicesDto> {
    return this.api.untypedGet<CharacterVoicesDto>(
      `${projectUrl(folder)}/characters/${characterId}/voices`,
    );
  }

  /** 404 for an unknown voice. */
  get(folder: string, voiceId: Guid): Promise<VoiceDto> {
    return this.api.untypedGet<VoiceDto>(`${projectUrl(folder)}/voices/${voiceId}`);
  }

  /**
   * Upload or replace the reference audio (200 MB max; the host normalises and commits). Answers
   * the updated voice. 400 on a missing file or unsupported format, 422 when the host refused it.
   */
  uploadAudio(folder: string, voiceId: Guid, file: File): Promise<VoiceDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.untypedPutForm<VoiceDto>(`${projectUrl(folder)}/voices/${voiceId}/audio`, form);
  }

  /** Transcribe the voice's audio with the active transcription service and persist it. 422 without audio. */
  transcribe(folder: string, voiceId: Guid): Promise<TranscribeVoiceResponse> {
    return this.api.untypedPost<TranscribeVoiceResponse>(
      `${projectUrl(folder)}/voices/${voiceId}/transcribe`,
    );
  }

  /** The voice-design prompt template rendered for the character; nothing persisted. */
  renderDesignPrompt(folder: string, characterId: Guid): Promise<RenderedDesignPromptResponse> {
    return this.api.untypedPost<RenderedDesignPromptResponse>(
      `${projectUrl(folder)}/characters/${characterId}/design-prompt/render`,
    );
  }

  /** Ask the LLM for a design prompt from a (possibly edited) rendered prompt. Synchronous; nothing persisted. */
  generateDesignPrompt(
    folder: string,
    characterId: Guid,
    prompt: string,
  ): Promise<GenerateDesignPromptResponse> {
    const request: GenerateDesignPromptRequest = { prompt };
    return this.api.untypedPost<GenerateDesignPromptResponse>(
      `${projectUrl(folder)}/characters/${characterId}/design-prompt/generate`,
      request,
    );
  }

  /**
   * Synchronous, tens of seconds. 422 when there is no design prompt or synthesis fails. A
   * `designPrompt` is designed from instead of the stored one and lands with the take, so an
   * unsaved edit and its audio are one request; without one the call sends no body.
   */
  generateAudio(
    folder: string,
    characterId: Guid,
    voiceId: Guid,
    designPrompt?: string,
  ): Promise<GenerateVoiceAudioResponse> {
    const body: GenerateVoiceAudioRequest | undefined =
      designPrompt === undefined ? undefined : { designPrompt };
    return this.api.untypedPost<GenerateVoiceAudioResponse>(
      `${projectUrl(folder)}/characters/${characterId}/voices/${voiceId}/generate-audio`,
      body,
    );
  }

  /** One LLM voice-plan call per character without voices. 409 while a batch is running. */
  startPromptBatch(folder: string, regenerateAll = false): Promise<StartedResponse> {
    return this.api.untypedPost<StartedResponse>(`${projectUrl(folder)}/voice-batch/prompts`, {
      regenerateAll,
    });
  }

  /** Synthesise every generated voice that has a prompt but no audio. 409 while a batch is running. */
  startAudioBatch(folder: string): Promise<StartedResponse> {
    return this.api.untypedPost<StartedResponse>(`${projectUrl(folder)}/voice-batch/audio`);
  }

  batchStatus(): Promise<VoiceBatchStatusDto> {
    return this.api.untypedGet<VoiceBatchStatusDto>('/api/voice-batch/status');
  }

  cancelBatch(): Promise<void> {
    return this.api.untypedPost<void>('/api/voice-batch/cancel');
  }
}
