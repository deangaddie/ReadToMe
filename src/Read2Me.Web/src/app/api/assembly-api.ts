import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { AssemblyOutputDto, AssemblyStatusDto, StartedResponse } from './dtos';
import { projectUrl } from './projects-api';

/** `AssemblyEndpoints.cs`: m4b assembly start, progress and cancel (one global run), and outputs. */
@Injectable({ providedIn: 'root' })
export class AssemblyApi {
  private readonly api = inject(ApiClient);

  /**
   * 202 when started. 409 either because a run is active or because items still lack audio and
   * `allowPartial` is false; in the latter case `ApiError.extensions['audioRemainingCount']` says
   * how many.
   */
  start(folder: string, allowPartial = false): Promise<StartedResponse> {
    return this.api.post<StartedResponse>(`${projectUrl(folder)}/assembly`, { allowPartial });
  }

  status(): Promise<AssemblyStatusDto> {
    return this.api.get<AssemblyStatusDto>('/api/assembly/status');
  }

  cancel(): Promise<void> {
    return this.api.post<void>('/api/assembly/cancel');
  }

  /** The project's assembled audiobooks, newest first. */
  outputs(folder: string): Promise<AssemblyOutputDto[]> {
    return this.api.get<AssemblyOutputDto[]>(`${projectUrl(folder)}/assembly/outputs`);
  }

  deleteOutput(folder: string, fileName: string): Promise<void> {
    return this.api.delete(assemblyOutputUrl(folder, fileName));
  }
}

/** Download link for one output: the host answers an `audio/mp4` attachment, so a plain anchor saves it. */
export function assemblyOutputUrl(folder: string, fileName: string): string {
  return `${projectUrl(folder)}/assembly/outputs/${encodeURIComponent(fileName)}`;
}
