import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  AudioEnqueueRequest,
  AudioItemStatusDto,
  AudioQueueSnapshot,
  AudioReviewsDto,
  EnqueueResponse,
  Guid,
} from './dtos';
import { projectUrl } from './projects-api';

/** `AudioEndpoints.cs` + the audio half of `QueueStatusEndpoints.cs`. */
@Injectable({ providedIn: 'root' })
export class AudioApi {
  private readonly api = inject(ApiClient);

  /** 202 with the number of items queued; 400 on an unknown level, 409 when no TTS config is active. */
  enqueue(folder: string, request: AudioEnqueueRequest): Promise<EnqueueResponse> {
    return this.api.post<EnqueueResponse>(`${projectUrl(folder)}/audio/enqueue`, request);
  }

  /** Sparse: only items whose audio needs review or had it dismissed. */
  reviews(folder: string): Promise<AudioReviewsDto> {
    return this.api.get<AudioReviewsDto>(`${projectUrl(folder)}/audio/reviews`);
  }

  itemStatus(folder: string, itemId: Guid): Promise<AudioItemStatusDto> {
    return this.api.get<AudioItemStatusDto>(`${projectUrl(folder)}/audio/items/${itemId}`);
  }

  queue(): Promise<AudioQueueSnapshot> {
    return this.api.get<AudioQueueSnapshot>('/api/audio/queue');
  }

  /** Global: drains every folder's queued audio work. */
  cancel(): Promise<void> {
    return this.api.post<void>('/api/audio/cancel');
  }
}
