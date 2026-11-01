import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import { ApiError } from './api-error';
import type { AiServiceDto, AiServiceStatusDto } from './dtos';

/**
 * `AiServiceEndpoints.cs`: the Docker AI service catalog, live health probes, and the manual
 * lifecycle ops. An op answers 202 and its outcome arrives as a `serviceStatus` hub message
 * (`{ name, status, op, ok, error? }`) — the store, not the caller, learns how it went.
 */
export class AiServicesApi {
  private readonly api = use(ApiClient);

  list(): Promise<AiServiceDto[]> {
    return this.api.get('/api/ai-services');
  }

  /** The managed service behind a config base URL, or null when the watchdog does not manage it (404). */
  async resolve(baseUrl: string): Promise<AiServiceDto | null> {
    try {
      return await this.api.get('/api/ai-services/resolve', { query: { baseUrl } });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }

  /** One health probe per managed service, in one call. */
  statusAll(): Promise<AiServiceStatusDto[]> {
    return this.api.get('/api/ai-services/status');
  }

  /** One health probe; 404 for a name not in the catalog. */
  status(name: string): Promise<AiServiceStatusDto> {
    return this.api.get('/api/ai-services/{name}/status', { path: { name } });
  }

  /** docker start → health → warm-up in the background (202); 409 while an op is already in flight. */
  start(name: string): Promise<void> {
    return this.api.post('/api/ai-services/{name}/start', { path: { name } });
  }

  restart(name: string): Promise<void> {
    return this.api.post('/api/ai-services/{name}/restart', { path: { name } });
  }

  shutdown(name: string): Promise<void> {
    return this.api.post('/api/ai-services/{name}/shutdown', { path: { name } });
  }
}
