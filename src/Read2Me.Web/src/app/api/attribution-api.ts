import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type {
  AttributionQueueSnapshot,
  EnqueueResponse,
  Guid,
  NodeEnqueueRequest,
  ParagraphAttributionStatusDto,
} from './dtos';

/** `AttributionEndpoints.cs` + the attribution half of `QueueStatusEndpoints.cs`. */
export class AttributionApi {
  private readonly api = use(ApiClient);

  /** 202 with the number of paragraphs queued; 400 on an unknown level. */
  enqueue(folder: string, request: NodeEnqueueRequest): Promise<EnqueueResponse> {
    return this.api.post('/api/projects/{folder}/attribution/enqueue', {
      path: { folder },
      body: request,
    });
  }

  /** A selection by id (ticket 12): 202 with how many were queued — ids without dialog are skipped. */
  enqueueParagraphs(folder: string, paragraphIds: Guid[]): Promise<EnqueueResponse> {
    return this.api.post('/api/projects/{folder}/attribution/enqueue-paragraphs', {
      path: { folder },
      body: { paragraphIds },
    });
  }

  paragraphStatus(folder: string, paragraphId: Guid): Promise<ParagraphAttributionStatusDto> {
    return this.api.get('/api/projects/{folder}/attribution/paragraphs/{paragraphId}', {
      path: { folder, paragraphId },
    });
  }

  /** Forgets a paragraph's Failed/Unfinished outcome; 204 whether or not one was recorded. */
  clearOutcome(folder: string, paragraphId: Guid): Promise<void> {
    return this.api.delete('/api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome', {
      path: { folder, paragraphId },
    });
  }

  queue(): Promise<AttributionQueueSnapshot> {
    return this.api.get('/api/attribution/queue');
  }

  /** Global: drains every folder's queued attribution work. */
  cancel(): Promise<void> {
    return this.api.post('/api/attribution/cancel');
  }

  /** Retires the finished run's throughput summary on the host (ticket 14); 204 whether or not one was showing. */
  dismiss(): Promise<void> {
    return this.api.post('/api/attribution/dismiss');
  }
}
