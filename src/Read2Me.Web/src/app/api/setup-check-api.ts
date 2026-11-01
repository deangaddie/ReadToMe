import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { SetupCheckReport } from './dtos';

/** `SetupCheckEndpoints.cs`: the Setup check report and the Quick check that refreshes it. */
export class SetupCheckApi {
  private readonly api = use(ApiClient);

  report(): Promise<SetupCheckReport> {
    return this.api.untypedGet<SetupCheckReport>('/api/setup-check');
  }

  /** Runs the Quick check (it never starts, stops or switches anything) and answers the new report. */
  runQuick(): Promise<SetupCheckReport> {
    return this.api.untypedPost<SetupCheckReport>('/api/setup-check/quick');
  }

  /**
   * Starts the Full check, or joins the run already going; answers the report with `fullRun`.
   * Rejects with a 409 problem naming the work in flight. Progress arrives on the hub as `setupCheck`.
   */
  startFull(): Promise<SetupCheckReport> {
    return this.api.untypedPost<SetupCheckReport>('/api/setup-check/full');
  }

  /**
   * Re-runs one Full-tier check under the Full check's rules; answers the report with `fullRun`.
   * Rejects with a 409 problem while a run goes or work is in flight, a 404 for a Quick-tier id.
   */
  rerun(checkId: string): Promise<SetupCheckReport> {
    return this.api.untypedPost<SetupCheckReport>(
      `/api/setup-check/checks/${encodeURIComponent(checkId)}/rerun`,
    );
  }

  /** Asks the Full check to stop once its current step ends; 409 when none is running. */
  cancelFull(): Promise<SetupCheckReport> {
    return this.api.untypedPost<SetupCheckReport>('/api/setup-check/full/cancel');
  }
}
