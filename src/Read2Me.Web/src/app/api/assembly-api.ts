import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type { AssemblyStatusDto, StartedResponse } from './dtos';
import { projectUrl } from './projects-api';

/** `AssemblyEndpoints.cs`: m4b assembly start, progress and cancel (one global run). */
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
}
