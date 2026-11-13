import { describe, expect, it } from 'bun:test';
import { deleteRefusedMessage } from './delete-refusal';

describe('deleteRefusedMessage()', () => {
  it.each([
    [
      'busy',
      '128 paragraphs · assembling',
      "Can't delete Pride and Prejudice: 128 paragraphs · assembling. Stop it in Activity, then delete.",
    ],
    [
      'restoring',
      'Project is restoring',
      "Can't delete Pride and Prejudice: Project is restoring. Delete it once the Restore has finished.",
    ],
    [
      'deleting',
      'Project is being deleted',
      "Can't delete Pride and Prejudice: Project is being deleted.",
    ],
    [undefined, 'Something else', "Can't delete Pride and Prejudice: Something else."],
  ])('for activity %p names the book, the reason and what to do', (activity, reason, message) => {
    expect(deleteRefusedMessage('Pride and Prejudice', reason, activity)).toBe(message);
  });

  it('keeps the reason as the host wrote it, without a doubled full stop', () => {
    expect(deleteRefusedMessage('Emma', ' Project is restoring. ', 'restoring')).toBe(
      "Can't delete Emma: Project is restoring. Delete it once the Restore has finished.",
    );
  });
});
