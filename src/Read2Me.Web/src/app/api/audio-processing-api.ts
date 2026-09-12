import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { AudioProcessingSettings, AudioProcessingUpdateRequest } from './dtos';

/** `SettingsEndpoints.MapAudioProcessingEndpoints`: the single audio post-processing row. */
@Injectable({ providedIn: 'root' })
export class AudioProcessingApi {
  private readonly api = inject(ApiClient);
  private readonly base = '/api/settings/audio-processing';

  get(): Promise<AudioProcessingSettings> {
    return this.api.get<AudioProcessingSettings>(this.base);
  }

  /** Only the supplied scalars change; the full row comes back. */
  update(patch: AudioProcessingUpdateRequest): Promise<AudioProcessingSettings> {
    return this.api.put<AudioProcessingSettings>(this.base, patch);
  }
}
