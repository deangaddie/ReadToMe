import { Injectable, inject } from '@angular/core';
import { ApiClient } from './api-client';
import type {
  BookEditRow,
  BookEditRunDto,
  Guid,
  PlanBookEditRequest,
  PlanBookEditResponse,
  ProposeBookEditRequest,
  ProposeOneBookEditRequest,
} from './dtos';
import { projectUrl } from './projects-api';

/**
 * `BookEditEndpoints.cs` (ticket 19): the AI book-edit flow. The plan lives on the host under an
 * opaque `program` id, so this client passes ids and rows around and never the plan itself. Apply
 * is not here — it is the ordinary `ApplyBookEdits` book command over the rows the user kept.
 */
@Injectable({ providedIn: 'root' })
export class BookEditsApi {
  private readonly api = inject(ApiClient);

  /** One LLM call plus scope resolution. Every non-`Ok` status is an answer, not an error. */
  plan(folder: string, instruction: string, thinking: boolean): Promise<PlanBookEditResponse> {
    const request: PlanBookEditRequest = { instruction, thinking };
    return this.api.post<PlanBookEditResponse>(`${this.base(folder)}/plan`, request);
  }

  /**
   * Starts the proposal run (202). Progress and the finished rows arrive on the hub as `bookEdit`
   * messages to `connectionId`; {@link run} reads the same state back. 409 while one is in flight.
   */
  propose(
    folder: string,
    program: string,
    thinking: boolean,
    connectionId: string | null,
  ): Promise<void> {
    const request: ProposeBookEditRequest = { thinking, connectionId };
    return this.api.post<void>(`${this.base(folder)}/${program}/propose`, request);
  }

  /** The run's status, progress and the rows it has landed. */
  run(folder: string, program: string): Promise<BookEditRunDto> {
    return this.api.get<BookEditRunDto>(`${this.base(folder)}/${program}`);
  }

  /** Re-asks the AI for one row, optionally steered by a hint. */
  proposeOne(
    folder: string,
    program: string,
    targetId: Guid,
    hint: string | null,
    thinking: boolean,
  ): Promise<BookEditRow> {
    const request: ProposeOneBookEditRequest = { targetId, hint, thinking };
    return this.api.post<BookEditRow>(`${this.base(folder)}/${program}/propose-one`, request);
  }

  /** Stops the running proposal; the rows computed so far stay reviewable. */
  cancel(folder: string, program: string): Promise<void> {
    return this.api.post<void>(`${this.base(folder)}/${program}/cancel`);
  }

  /** Drops the session, cancelling any run in flight. */
  discard(folder: string, program: string): Promise<void> {
    return this.api.delete(`${this.base(folder)}/${program}`);
  }

  private base(folder: string): string {
    return `${projectUrl(folder)}/book-edits`;
  }
}
