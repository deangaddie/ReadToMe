import type { PermanentChange } from '@app/ui/dialogs';

/**
 * The attribution chain's permanent-change confirm (guidance-primitives spec §7): Remove on a rung
 * always asks and is red, since the rung's config × thinking × style choice is discarded.
 */
export function removeStepChange(index: number, configName: string): PermanentChange {
  return {
    action: 'Remove step',
    scope: `Removes step ${index + 1} (${configName}) and its settings.`,
    cost: 'Adding it back means choosing its config, thinking and style again.',
    destructive: true,
  };
}
