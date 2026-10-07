import { type AiTaskKind, PreflightApi, toApiError } from '@app/api';
import { openDialog } from '@app/core/dialog';
import { use } from '@app/core/services';
import { ToastService } from '@app/ui/toast';
import '@app/ui/preflight-sheet';

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
 * the `r2m-preflight-sheet` opens as a bottom sheet (`openDialog`, docked, no light dismiss) with
 * the services to start and the GPU conflicts to stop, and the run (`/run`, progress over the hub)
 * decides. Cancelling never starts anything.
 */
export class Preflight {
  private readonly api = use(PreflightApi);
  private readonly toast = use(ToastService);

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

    const sheet = document.createElement('r2m-preflight-sheet');
    sheet.data = { kind, label, plan };
    return (await openDialog<boolean>(sheet, { variant: 'sheet', closedBy: 'none' })) === true;
  }
}
