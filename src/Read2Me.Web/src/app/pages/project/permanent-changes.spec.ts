import { describe, expect, it } from 'bun:test';
import { removeCoverChange, rereadConfirm } from './permanent-changes';

describe('rereadConfirm (guidance-primitives 14)', () => {
  const body =
    'This deletes all book data — attribution, generated audio, and edits — and reprocesses the ' +
    'source file from scratch.';

  it('keeps the Reread confirm as it was when no step is confirmed or skipped', () => {
    expect(rereadConfirm({})).toEqual({
      title: 'Reread the book?',
      message: `${body} This cannot be undone.`,
      destructive: true,
      confirmLabel: 'Delete and reread',
    });
  });

  it('says that the confirmed and skipped steps go when any checkpoint row exists', () => {
    expect(
      rereadConfirm({ structure: { state: 'skipped', stale: false, at: '2026-10-18T00:00:00Z' } })
        .message,
    ).toBe(`${body} Clears your confirmed and skipped steps. This cannot be undone.`);
  });
});

describe('removeCoverChange (guidance-primitives 14)', () => {
  it('always asks, red: the image the user uploaded goes', () => {
    expect(removeCoverChange()).toEqual({
      action: 'Remove cover',
      scope: 'Deletes the cover image.',
      cost: 'Getting it back means uploading it again.',
      destructive: true,
    });
  });
});
