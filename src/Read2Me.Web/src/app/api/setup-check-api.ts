import { use } from '@app/core/services';
import { ApiClient } from './api-client';
import type { SetupCheckReport } from './dtos';

/** `SetupCheckEndpoints.cs`: the Setup check report and the Quick check that refreshes it. */
export class SetupCheckApi {
  private readonly api = use(ApiClient);

  report(): Promise<SetupCheckReport> {
    return this.api.get<SetupCheckReport>('/api/setup-check');
  }

  /** Runs the Quick check (it never starts, stops or switches anything) and answers the new report. */
  runQuick(): Promise<SetupCheckReport> {
    return this.api.post<SetupCheckReport>('/api/setup-check/quick');
  }
}
