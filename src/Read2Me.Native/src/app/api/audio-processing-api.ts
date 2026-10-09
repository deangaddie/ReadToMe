import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { AudioProcessingSettings } from './dtos';

/**
 * `AudioProcessingEndpoints.cs`: the scalar row (ffmpeg path, WER threshold, pauses). The
 * export page reads it for its readiness card; the Settings PR adds the writes.
 */
export class AudioProcessingApi {
  private readonly api = use(ApiClient);
  private readonly base = '/api/settings/audio-processing';

  get(): Promise<AudioProcessingSettings> {
    return this.api.get<AudioProcessingSettings>(this.base);
  }
}
