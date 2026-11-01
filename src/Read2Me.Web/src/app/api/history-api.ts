import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { Guid, HistoryDto, HistoryStepDto, RestoreResultDto } from './dtos';

/** `HistoryEndpoints.cs`: a project's History. */
export class HistoryApi {
  private readonly api = use(ApiClient);

  /** Steps newest first, the Head, the busy reason and the restoring flag. The first call starts the project's History. */
  list(folder: string): Promise<HistoryDto> {
    return this.api.get('/api/projects/{folder}/history', { path: { folder } });
  }

  /** One step with its Restore preview (how many items would lose audio). 409 while the project is busy. */
  step(folder: string, stepId: Guid): Promise<HistoryStepDto> {
    return this.api.get('/api/projects/{folder}/history/{stepId}', { path: { folder, stepId } });
  }

  /** Restore: the project as it stood after the step becomes current, as a new step. 409 while busy or restoring. */
  restore(folder: string, stepId: Guid): Promise<RestoreResultDto> {
    return this.api.post('/api/projects/{folder}/history/{stepId}/restore', {
      path: { folder, stepId },
    });
  }
}
