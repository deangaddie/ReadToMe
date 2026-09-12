import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  CharacterVoicesDto,
  GenerateVoiceAudioResponse,
  Guid,
  StartedResponse,
  VoiceBatchStatusDto,
} from './dtos';
import { projectUrl } from './projects-api';

/** `VoiceEndpoints.cs`: per-character voice reads, single-voice synthesis and the two voice batches. */
@Injectable({ providedIn: 'root' })
export class VoicesApi {
  private readonly api = inject(ApiClient);

  list(folder: string, characterId: Guid): Promise<CharacterVoicesDto> {
    return this.api.get<CharacterVoicesDto>(
      `${projectUrl(folder)}/characters/${characterId}/voices`,
    );
  }

  /** Synchronous, tens of seconds. 422 when the voice has no design prompt or synthesis fails. */
  generateAudio(
    folder: string,
    characterId: Guid,
    voiceId: Guid,
  ): Promise<GenerateVoiceAudioResponse> {
    return this.api.post<GenerateVoiceAudioResponse>(
      `${projectUrl(folder)}/characters/${characterId}/voices/${voiceId}/generate-audio`,
    );
  }

  /** One LLM voice-plan call per character without voices. 409 while a batch is running. */
  startPromptBatch(folder: string, regenerateAll = false): Promise<StartedResponse> {
    return this.api.post<StartedResponse>(`${projectUrl(folder)}/voice-batch/prompts`, {
      regenerateAll,
    });
  }

  /** Synthesise every generated voice that has a prompt but no audio. 409 while a batch is running. */
  startAudioBatch(folder: string): Promise<StartedResponse> {
    return this.api.post<StartedResponse>(`${projectUrl(folder)}/voice-batch/audio`);
  }

  batchStatus(): Promise<VoiceBatchStatusDto> {
    return this.api.get<VoiceBatchStatusDto>('/api/voice-batch/status');
  }

  cancelBatch(): Promise<void> {
    return this.api.post<void>('/api/voice-batch/cancel');
  }
}
