import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { ApplyDiscoveryResponse, ApplyDiscoveryRow, DiscoveryOutcomeDto } from './dtos';
import { projectUrl } from './projects-api';

/** `DiscoveryEndpoints.cs`: synchronous LLM character discovery and its apply step. */
@Injectable({ providedIn: 'root' })
export class DiscoveryApi {
  private readonly api = inject(ApiClient);

  /** Seconds to a minute; `thinking` trades speed for recall. 422 when no LLM server is active. */
  discover(folder: string, thinking = false): Promise<DiscoveryOutcomeDto> {
    return this.api.post<DiscoveryOutcomeDto>(
      `${projectUrl(folder)}/characters/discover`,
      undefined,
      { thinking },
    );
  }

  /** One create per row, idempotent on name/alias match. */
  apply(folder: string, rows: ApplyDiscoveryRow[]): Promise<ApplyDiscoveryResponse> {
    return this.api.post<ApplyDiscoveryResponse>(
      `${projectUrl(folder)}/characters/discover/apply`,
      rows,
    );
  }
}
