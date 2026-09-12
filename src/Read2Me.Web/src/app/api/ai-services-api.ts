import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { AiServiceDto, AiServiceStatusDto } from './dtos';

/** `AiServiceEndpoints.cs`: the Docker AI service catalog and a single live health probe. */
@Injectable({ providedIn: 'root' })
export class AiServicesApi {
  private readonly api = inject(ApiClient);

  list(): Promise<AiServiceDto[]> {
    return this.api.get<AiServiceDto[]>('/api/ai-services');
  }

  /** One health probe; 404 for a name not in the catalog. */
  status(name: string): Promise<AiServiceStatusDto> {
    return this.api.get<AiServiceStatusDto>(`/api/ai-services/${encodeURIComponent(name)}/status`);
  }
}
