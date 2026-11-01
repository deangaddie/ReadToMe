import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { ApplyDiscoveryResponse, ApplyDiscoveryRow, DiscoveryOutcomeDto } from './dtos';

/** `DiscoveryEndpoints.cs`: synchronous LLM character discovery and its apply step. */
export class DiscoveryApi {
  private readonly api = use(ApiClient);

  /** Seconds to a minute; `thinking` trades speed for recall. 422 when no LLM server is active. */
  discover(folder: string, thinking = false): Promise<DiscoveryOutcomeDto> {
    return this.api.post('/api/projects/{folder}/characters/discover', {
      path: { folder },
      query: { thinking },
    });
  }

  /** One create per row, idempotent on name/alias match. */
  apply(folder: string, rows: ApplyDiscoveryRow[]): Promise<ApplyDiscoveryResponse> {
    return this.api.post('/api/projects/{folder}/characters/discover/apply', {
      path: { folder },
      body: rows,
    });
  }
}
