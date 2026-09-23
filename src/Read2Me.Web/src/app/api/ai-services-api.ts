import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type { AiServiceDto, AiServiceStatusDto } from './dtos';

/**
 * `AiServiceEndpoints.cs`: the Docker AI service catalog, live health probes, and the manual
 * lifecycle ops. An op answers 202 and its outcome arrives as a `serviceStatus` hub message
 * (`{ name, status, op, ok, error? }`) — the store, not the caller, learns how it went.
 */
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

  /** One health probe per managed service, in one call. */
  statusAll(): Promise<AiServiceStatusDto[]> {
    return this.api.get<AiServiceStatusDto[]>('/api/ai-services/status');
  }

  /** One health probe; 404 for a name not in the catalog. */
  status(name: string): Promise<AiServiceStatusDto> {
    return this.api.get<AiServiceStatusDto>(`/api/ai-services/${encodeURIComponent(name)}/status`);
  }

  /** docker start → health → warm-up in the background (202); 409 while an op is already in flight. */
  start(name: string): Promise<void> {
    return this.api.post<void>(`/api/ai-services/${encodeURIComponent(name)}/start`);
  }

  restart(name: string): Promise<void> {
    return this.api.post<void>(`/api/ai-services/${encodeURIComponent(name)}/restart`);
  }

  shutdown(name: string): Promise<void> {
    return this.api.post<void>(`/api/ai-services/${encodeURIComponent(name)}/shutdown`);
  }
}
