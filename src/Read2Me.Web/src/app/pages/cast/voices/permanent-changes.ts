import type { PermanentChange } from '@app/ui/dialogs';

/**
 * The voice card's permanent-change confirm (guidance-primitives spec §7): Send to AI (transcribe)
 * asks only when a transcript exists, since the AI's answer replaces it; a blank one loses nothing.
 */
export function transcribeChange(currentTranscript: string): PermanentChange | null {
  if (currentTranscript.trim().length === 0) return null;
  return {
    action: 'Send to AI',
    scope: 'Replaces the current transcript.',
    cost: 'The text it replaces is not kept.',
    destructive: true,
  };
}
