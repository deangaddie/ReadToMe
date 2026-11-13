/**
 * The toast for a project delete the host refused (409, #346). Nothing was cancelled; the advice
 * follows the 409's `activity` extension, the state that refused it. `reason` is the host's
 * `detail`, shown as written.
 */
const advice: Record<string, string> = {
  busy: ' Stop it in Activity, then delete.',
  restoring: ' Delete it once the Restore has finished.',
};

export function deleteRefusedMessage(title: string, reason: string, activity: unknown): string {
  const next = typeof activity === 'string' ? (advice[activity] ?? '') : '';
  return `Can't delete ${title}: ${reason.trim().replace(/\.$/, '')}.${next}`;
}
