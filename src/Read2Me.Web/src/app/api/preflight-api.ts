import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  AiTaskKind,
  PreflightPlanDto,
  PreflightRunRequest,
  PreflightRunResponse,
} from './dtos';

/**
 * `PreflightEndpoints.cs` (ticket 25): what a task needs before it may touch its AI services, and
 * the background run that reconciles it. Progress arrives as `preflight` hub messages on the
 * connection named in the run request.
 */
@Injectable({ providedIn: 'root' })
export class PreflightApi {
  private readonly api = inject(ApiClient);

  plan(task: AiTaskKind): Promise<PreflightPlanDto> {
    return this.api.post<PreflightPlanDto>(`/api/preflight/${task}/plan`);
  }

  /** 202 with the run id; every message of the run carries it. */
  run(task: AiTaskKind, request: PreflightRunRequest): Promise<PreflightRunResponse> {
    return this.api.post<PreflightRunResponse>(`/api/preflight/${task}/run`, request);
  }
}
