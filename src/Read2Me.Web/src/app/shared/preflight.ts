import { Injectable, inject } from '@angular/core';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { firstValueFrom } from 'rxjs';
import { AiTaskKind, PreflightApi, toApiError } from '@app/api';
import { ToastService } from '@app/ui/toast/toast.service';
import { PreflightSheetData, PreflightSheetHost } from './preflight-sheet-host';

/** AI task kinds a preflight checks services for (`IAiPreflight`, spec D10). */
export type PreflightTask =
  | 'attribution'
  | 'audio'
  | 'discovery'
  | 'voicePrompt'
  | 'voiceDesign'
  | 'transcription'
  | 'bookEdit';

/** Host `AiTaskKind` and the label the sheet shows, per task. */
export const PREFLIGHT_TASKS: Record<PreflightTask, { kind: AiTaskKind; label: string }> = {
  attribution: { kind: 'CharacterAttribution', label: 'Attribution' },
  audio: { kind: 'AudioGeneration', label: 'Audio generation' },
  discovery: { kind: 'CharacterDiscovery', label: 'Character discovery' },
  voicePrompt: { kind: 'VoicePromptGeneration', label: 'Voice prompt generation' },
  voiceDesign: { kind: 'VoiceDesignAudio', label: 'Voice audio generation' },
  transcription: { kind: 'Transcription', label: 'Transcription' },
  bookEdit: { kind: 'BookEdit', label: 'Edit with AI' },
};

/**
 * Design principle 4: every AI action is preceded by readiness. Asks the host what the task needs
 * (`POST /api/preflight/{kind}/plan`); when everything is up the caller proceeds at once, otherwise
 * the `r2m-preflight-sheet` shows the services to start and the GPU conflicts to stop, and the run
 * (`/run`, progress over the hub) decides. Cancelling never starts anything.
 */
@Injectable({ providedIn: 'root' })
export class Preflight {
  private readonly api = inject(PreflightApi);
  private readonly sheet = inject(MatBottomSheet);
  private readonly toast = inject(ToastService);

  /** Resolves true when the task may run, false when the user cancelled or a service failed to start. */
  async ensureReady(task: PreflightTask): Promise<boolean> {
    const { kind, label } = PREFLIGHT_TASKS[task];
    let plan;
    try {
      plan = await this.api.plan(kind);
    } catch (error) {
      this.toast.problem(toApiError(error).toProblem());
      return false;
    }
    if (plan.ready) return true;

    const data: PreflightSheetData = { kind, label, plan };
    const ref = this.sheet.open<PreflightSheetHost, PreflightSheetData, boolean>(
      PreflightSheetHost,
      {
        data,
        disableClose: true,
        panelClass: 'r2m-preflight-panel',
      },
    );
    return (await firstValueFrom(ref.afterDismissed())) === true;
  }
}
