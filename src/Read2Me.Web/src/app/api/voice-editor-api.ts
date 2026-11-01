import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  Guid,
  PreviewResponse,
  PreviewStepRequest,
  StepCatalogEntryDto,
  VoiceDto,
} from './dtos';
import { projectUrl } from './projects-api';
import { withViewedStep } from './step-view';

/** The stored original of an edited voice (404 while the voice is unedited), at the viewed step if any. */
export function voiceOriginalUrl(folder: string, voiceId: Guid): string {
  return withViewedStep(`${projectUrl(folder)}/voices/${voiceId}/original.wav`, folder);
}

/**
 * `VoiceEditorEndpoints.cs` (ticket 18): the voice-scope step catalog, preview → per-stage WAVs,
 * apply by `previewId` (the host guarantees you apply what you heard), restore.
 */
export class VoiceEditorApi {
  private readonly api = use(ApiClient);

  /** The voice-scope steps in chain order, each with dials and defaults. */
  catalog(): Promise<StepCatalogEntryDto[]> {
    return this.api.get('/api/audio/steps/catalog', { query: { scope: 'voice' } });
  }

  /** Render the ticked steps over the voice's original. 400 on an empty or unknown chain, 422 without audio. */
  preview(folder: string, voiceId: Guid, steps: PreviewStepRequest[]): Promise<PreviewResponse> {
    return this.api.post('/api/projects/{folder}/voices/{voiceId}/editor/preview', {
      path: { folder, voiceId },
      body: { steps },
    });
  }

  /** Write a preview's final stage over the voice. 422 when the preview expired or is another voice's. */
  apply(folder: string, voiceId: Guid, previewId: string): Promise<VoiceDto> {
    return this.api.post('/api/projects/{folder}/voices/{voiceId}/editor/apply', {
      path: { folder, voiceId },
      body: { previewId },
    });
  }

  /** Put the original back and forget the edit. */
  restore(folder: string, voiceId: Guid): Promise<VoiceDto> {
    return this.api.post('/api/projects/{folder}/voices/{voiceId}/editor/restore', {
      path: { folder, voiceId },
    });
  }
}
