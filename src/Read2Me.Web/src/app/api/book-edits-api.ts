import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { BookEditRowDto, BookEditRunDto, Guid, PlanBookEditResponse } from './dtos';

/**
 * `BookEditEndpoints.cs` (ticket 19): the AI book-edit flow. The plan lives on the host under an
 * opaque `program` id, so this client passes ids and rows around and never the plan itself. Apply
 * is not here — it is the ordinary `ApplyBookEdits` book command over the rows the user kept.
 */
export class BookEditsApi {
  private readonly api = use(ApiClient);

  /** One LLM call plus scope resolution. Every non-`Ok` status is an answer, not an error. */
  plan(folder: string, instruction: string, thinking: boolean): Promise<PlanBookEditResponse> {
    return this.api.post('/api/projects/{folder}/book-edits/plan', {
      path: { folder },
      body: { instruction, thinking },
    });
  }

  /**
   * Starts the proposal run (202). Progress and the finished rows arrive on the hub as `bookEdit`
   * messages to `connectionId`; {@link run} reads the same state back. 409 while one is in flight.
   */
  async propose(
    folder: string,
    program: string,
    thinking: boolean,
    connectionId: string | null,
  ): Promise<void> {
    await this.api.post('/api/projects/{folder}/book-edits/{program}/propose', {
      path: { folder, program },
      body: { thinking, connectionId },
    });
  }

  /** The run's status, progress and the rows it has landed. */
  run(folder: string, program: string): Promise<BookEditRunDto> {
    return this.api.get('/api/projects/{folder}/book-edits/{program}', {
      path: { folder, program },
    });
  }

  /** Re-asks the AI for one row, optionally steered by a hint. */
  proposeOne(
    folder: string,
    program: string,
    targetId: Guid,
    hint: string | null,
    thinking: boolean,
  ): Promise<BookEditRowDto> {
    return this.api.post('/api/projects/{folder}/book-edits/{program}/propose-one', {
      path: { folder, program },
      body: { targetId, hint, thinking },
    });
  }

  /** Stops the running proposal; the rows computed so far stay reviewable. */
  cancel(folder: string, program: string): Promise<void> {
    return this.api.post('/api/projects/{folder}/book-edits/{program}/cancel', {
      path: { folder, program },
    });
  }

  /** Drops the session, cancelling any run in flight. */
  discard(folder: string, program: string): Promise<void> {
    return this.api.delete('/api/projects/{folder}/book-edits/{program}', {
      path: { folder, program },
    });
  }
}
