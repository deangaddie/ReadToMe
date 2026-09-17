import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  ApplyPreviewRequest,
  Guid,
  PreviewRequest,
  PreviewResponse,
  PreviewStepRequest,
  StepCatalogEntryDto,
  VoiceDto,
} from './dtos';
import { projectUrl } from './projects-api';

/** The stored original of an edited voice (404 while the voice is unedited). */
export function voiceOriginalUrl(folder: string, voiceId: Guid): string {
  return `${projectUrl(folder)}/voices/${voiceId}/original.wav`;
}

/**
 * `VoiceEditorEndpoints.cs` (ticket 18): the voice-scope step catalog, preview → per-stage WAVs,
 * apply by `previewId` (the host guarantees you apply what you heard), restore.
 */
@Injectable({ providedIn: 'root' })
export class VoiceEditorApi {
  private readonly api = inject(ApiClient);

  /** The voice-scope steps in chain order, each with dials and defaults. */
  catalog(): Promise<StepCatalogEntryDto[]> {
    return this.api.get<StepCatalogEntryDto[]>('/api/audio/steps/catalog', { scope: 'voice' });
  }

  /** Render the ticked steps over the voice's original. 400 on an empty or unknown chain, 422 without audio. */
  preview(folder: string, voiceId: Guid, steps: PreviewStepRequest[]): Promise<PreviewResponse> {
    const request: PreviewRequest = { steps };
    return this.api.post<PreviewResponse>(
      `${projectUrl(folder)}/voices/${voiceId}/editor/preview`,
      request,
    );
  }

  /** Write a preview's final stage over the voice. 422 when the preview expired or is another voice's. */
  apply(folder: string, voiceId: Guid, previewId: string): Promise<VoiceDto> {
    const request: ApplyPreviewRequest = { previewId };
    return this.api.post<VoiceDto>(`${projectUrl(folder)}/voices/${voiceId}/editor/apply`, request);
  }

  /** Put the original back and forget the edit. */
  restore(folder: string, voiceId: Guid): Promise<VoiceDto> {
    return this.api.post<VoiceDto>(`${projectUrl(folder)}/voices/${voiceId}/editor/restore`);
  }
}
