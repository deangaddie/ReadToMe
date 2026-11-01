import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  AudioEnqueueRequest,
  AudioItemStatusDto,
  AudioQueueSnapshot,
  AudioReviewsDto,
  EnqueueResponse,
  Guid,
} from './dtos';

/** `AudioEndpoints.cs` + the audio half of `QueueStatusEndpoints.cs`. */
export class AudioApi {
  private readonly api = use(ApiClient);

  /** 202 with the number of items queued; 400 on an unknown level, 409 when no TTS config is active. */
  enqueue(folder: string, request: AudioEnqueueRequest): Promise<EnqueueResponse> {
    return this.api.post('/api/projects/{folder}/audio/enqueue', {
      path: { folder },
      body: request,
    });
  }

  /** A selection or a retry by item id (ticket 13): 202 with how many were queued; 409 when no TTS config is active. */
  enqueueItems(folder: string, itemIds: Guid[]): Promise<EnqueueResponse> {
    return this.api.post('/api/projects/{folder}/audio/enqueue-items', {
      path: { folder },
      body: { itemIds },
    });
  }

  /** Sparse: only items whose audio needs review or had it dismissed. */
  reviews(folder: string): Promise<AudioReviewsDto> {
    return this.api.get('/api/projects/{folder}/audio/reviews', { path: { folder } });
  }

  itemStatus(folder: string, itemId: Guid): Promise<AudioItemStatusDto> {
    return this.api.get('/api/projects/{folder}/audio/items/{itemId}', {
      path: { folder, itemId },
    });
  }

  queue(): Promise<AudioQueueSnapshot> {
    return this.api.get('/api/audio/queue');
  }

  /** Global: drains every folder's queued audio work. */
  cancel(): Promise<void> {
    return this.api.post('/api/audio/cancel');
  }
}
