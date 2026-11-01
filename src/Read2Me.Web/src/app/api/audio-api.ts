import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  AudioEnqueueRequest,
  AudioItemStatusDto,
  AudioQueueSnapshot,
  AudioReviewsDto,
  EnqueueResponse,
  Guid,
  ItemsEnqueueRequest,
} from './dtos';
import { projectUrl } from './projects-api';

/** `AudioEndpoints.cs` + the audio half of `QueueStatusEndpoints.cs`. */
export class AudioApi {
  private readonly api = use(ApiClient);

  /** 202 with the number of items queued; 400 on an unknown level, 409 when no TTS config is active. */
  enqueue(folder: string, request: AudioEnqueueRequest): Promise<EnqueueResponse> {
    return this.api.untypedPost<EnqueueResponse>(`${projectUrl(folder)}/audio/enqueue`, request);
  }

  /** A selection or a retry by item id (ticket 13): 202 with how many were queued; 409 when no TTS config is active. */
  enqueueItems(folder: string, itemIds: Guid[]): Promise<EnqueueResponse> {
    const request: ItemsEnqueueRequest = { itemIds };
    return this.api.untypedPost<EnqueueResponse>(
      `${projectUrl(folder)}/audio/enqueue-items`,
      request,
    );
  }

  /** Sparse: only items whose audio needs review or had it dismissed. */
  reviews(folder: string): Promise<AudioReviewsDto> {
    return this.api.untypedGet<AudioReviewsDto>(`${projectUrl(folder)}/audio/reviews`);
  }

  itemStatus(folder: string, itemId: Guid): Promise<AudioItemStatusDto> {
    return this.api.untypedGet<AudioItemStatusDto>(`${projectUrl(folder)}/audio/items/${itemId}`);
  }

  queue(): Promise<AudioQueueSnapshot> {
    return this.api.get('/api/audio/queue');
  }

  /** Global: drains every folder's queued audio work. */
  cancel(): Promise<void> {
    return this.api.untypedPost<void>('/api/audio/cancel');
  }
}
