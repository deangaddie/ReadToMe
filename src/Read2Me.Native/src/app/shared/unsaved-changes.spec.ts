import { describe, expect, it } from 'bun:test';
import type { ConfirmOptions, ConfirmService } from '@app/ui/dialogs';
import { DIRTY_GUARD_MESSAGE, confirmDiscard } from './unsaved-changes';

describe('confirmDiscard', () => {
  it('asks the destructive discard question and passes the answer through', async () => {
    const asked: ConfirmOptions[] = [];
    const confirm = {
      confirm: async (o: ConfirmOptions) => {
        asked.push(o);
        return false;
      },
    } as unknown as ConfirmService;

    expect(await confirmDiscard(confirm)).toBe(false);
    expect(asked).toEqual([
      {
        title: 'Discard changes?',
        message: DIRTY_GUARD_MESSAGE,
        confirmLabel: 'Discard',
        destructive: true,
      },
    ]);
    expect(DIRTY_GUARD_MESSAGE).toContain('unsaved changes');
  });
});
