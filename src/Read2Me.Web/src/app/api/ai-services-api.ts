import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type { AiServiceDto, AiServiceStatusDto } from './dtos';

/** `AiServiceEndpoints.cs`: the Docker AI service catalog and a single live health probe. */
@Injectable({ providedIn: 'root' })
export class AiServicesApi {
  private readonly api = inject(ApiClient);

  list(): Promise<AiServiceDto[]> {
    return this.api.get<AiServiceDto[]>('/api/ai-services');
  }

  /** The managed service behind a config base URL, or null when the watchdog does not manage it (404). */
  async resolve(baseUrl: string): Promise<AiServiceDto | null> {
    try {
      return await this.api.get<AiServiceDto>('/api/ai-services/resolve', { baseUrl });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }

  /** One health probe; 404 for a name not in the catalog. */
  status(name: string): Promise<AiServiceStatusDto> {
    return this.api.get<AiServiceStatusDto>(`/api/ai-services/${encodeURIComponent(name)}/status`);
  }
}
