import { Injectable } from '@angular/core';

/** AI task kinds a preflight checks services for (`IAiPreflight`, spec D10). */
export type PreflightTask =
  | 'attribution'
  | 'audio'
  | 'discovery'
  | 'voicePrompt'
  | 'voiceDesign'
  | 'transcription'
  | 'assembly';

/**
 * Design principle 4: every AI action is preceded by readiness. Callers already go through this
 * seam so ticket 25 can open the preflight sheet here without touching them; until then every
 * task is reported ready.
 */
@Injectable({ providedIn: 'root' })
export class Preflight {
  /** Resolves true when the task may run, false when the user cancelled. */
  ensureReady(task: PreflightTask): Promise<boolean> {
    void task;
    return Promise.resolve(true);
  }
}
