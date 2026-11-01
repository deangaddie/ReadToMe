import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { AssemblyOutputDto, AssemblyStatusDto, StartedResponse } from './dtos';
import { projectUrl } from './projects-api';

/** `AssemblyEndpoints.cs`: m4b assembly start, progress and cancel (one global run), and outputs. */
export class AssemblyApi {
  private readonly api = use(ApiClient);

  /**
   * 202 when started. 409 either because a run is active or because items still lack audio and
   * `allowPartial` is false; in the latter case `ApiError.extensions['audioRemainingCount']` says
   * how many.
   */
  start(folder: string, allowPartial = false): Promise<StartedResponse> {
    return this.api.post('/api/projects/{folder}/assembly', {
      path: { folder },
      body: { allowPartial },
    });
  }

  status(): Promise<AssemblyStatusDto> {
    return this.api.get('/api/assembly/status');
  }

  cancel(): Promise<void> {
    return this.api.post('/api/assembly/cancel');
  }

  /** The project's assembled audiobooks, newest first. */
  outputs(folder: string): Promise<AssemblyOutputDto[]> {
    return this.api.get('/api/projects/{folder}/assembly/outputs', { path: { folder } });
  }

  deleteOutput(folder: string, fileName: string): Promise<void> {
    return this.api.delete('/api/projects/{folder}/assembly/outputs/{fileName}', {
      path: { folder, fileName },
    });
  }
}

/** Download link for one output: the host answers an `audio/mp4` attachment, so a plain anchor saves it. */
export function assemblyOutputUrl(folder: string, fileName: string): string {
  return `${projectUrl(folder)}/assembly/outputs/${encodeURIComponent(fileName)}`;
}
