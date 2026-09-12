import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  AttributionQueueSnapshot,
  EnqueueResponse,
  Guid,
  NodeEnqueueRequest,
  ParagraphAttributionStatusDto,
} from './dtos';
import { projectUrl } from './projects-api';

/** `AttributionEndpoints.cs` + the attribution half of `QueueStatusEndpoints.cs`. */
@Injectable({ providedIn: 'root' })
export class AttributionApi {
  private readonly api = inject(ApiClient);

  /** 202 with the number of paragraphs queued; 400 on an unknown level. */
  enqueue(folder: string, request: NodeEnqueueRequest): Promise<EnqueueResponse> {
    return this.api.post<EnqueueResponse>(`${projectUrl(folder)}/attribution/enqueue`, request);
  }

  paragraphStatus(folder: string, paragraphId: Guid): Promise<ParagraphAttributionStatusDto> {
    return this.api.get<ParagraphAttributionStatusDto>(
      `${projectUrl(folder)}/attribution/paragraphs/${paragraphId}`,
    );
  }

  queue(): Promise<AttributionQueueSnapshot> {
    return this.api.get<AttributionQueueSnapshot>('/api/attribution/queue');
  }

  /** Global: drains every folder's queued attribution work. */
  cancel(): Promise<void> {
    return this.api.post<void>('/api/attribution/cancel');
  }
}
