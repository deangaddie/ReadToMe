import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { HistoryDto } from './dtos';
import { projectUrl } from './projects-api';

/** `HistoryEndpoints.cs`: a project's History. */
export class HistoryApi {
  private readonly api = use(ApiClient);

  /** Steps newest first, the Head, the busy reason and the restoring flag. The first call starts the project's History. */
  list(folder: string): Promise<HistoryDto> {
    return this.api.get<HistoryDto>(`${projectUrl(folder)}/history`);
  }
}
