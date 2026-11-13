import { describe, expect, it } from 'bun:test';
import type { ProjectsApi } from '@app/api';
import { addPausesAction, rereadAction } from './book-actions';

describe('addPausesAction', () => {
  it.each([
    [3, 'Inserts a pause between paragraphs and at the end of every chapter (3 chapters).'],
    [1, 'Inserts a pause between paragraphs and at the end of every chapter (1 chapter).'],
    [0, 'Inserts a pause between paragraphs and at the end of every chapter (0 chapters).'],
  ])('always asks, neutral, counting %i chapters', (chapters, scope) => {
    expect(addPausesAction.confirm(chapters)).toEqual({
      action: 'Add pauses',
      scope,
      cost: 'Removing them later means deleting each one.',
      destructive: false,
    });
  });

  it('runs the AddPauses command once confirmed', () => {
    expect(addPausesAction.command).toEqual({ type: 'AddPauses' });
  });
});

describe('rereadAction (guidance-primitives 14)', () => {
  const body =
    'This deletes all book data — attribution, generated audio, and edits — and reprocesses the ' +
    'source file from scratch.';

  it('keeps the Reread confirm as it was when no step is confirmed or skipped', () => {
    expect(rereadAction.confirm({})).toEqual({
      title: 'Reread the book?',
      message: `${body} This cannot be undone.`,
      destructive: true,
      confirmLabel: 'Delete and reread',
    });
  });

  it('says that the confirmed and skipped steps go when any checkpoint row exists', () => {
    expect(
      rereadAction.confirm({
        Structure: { state: 'Skipped', stale: false, at: '2026-10-18T00:00:00Z' },
      }).message,
    ).toBe(`${body} Clears your confirmed and skipped steps. This cannot be undone.`);
  });

  it('runs a reread import of the folder once confirmed', async () => {
    const calls: unknown[][] = [];
    const projects = {
      import: async (...args: unknown[]) => void calls.push(args),
    } as unknown as ProjectsApi;
    await rereadAction.command(projects, 'dracula');
    expect(calls).toEqual([['dracula', true]]);
  });
});
