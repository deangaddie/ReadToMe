import type { BookCommand, ProjectStatusDto, ProjectsApi } from '@app/api';
import type { ConfirmOptions, PermanentChange } from '@app/ui/dialogs';
import { plural } from './permanent-changes';

/**
 * The Book actions that run from both the Book page and the Overview (its rows and the next-step
 * pill, through `StepRunner`): each pairs its permanent-change confirm (guidance-primitives spec
 * §7) with the command it runs once the user confirms. Each caller keeps its own write path, busy
 * state, success toast and chapter count; those differ on purpose. The Book page runs Reread's
 * command through `BookEditor.reread`, `StepRunner` calls it with its own `ProjectsApi`.
 */

/** Add pauses always asks and is neutral: nothing is lost, but each pause is a delete to undo. */
export const addPausesAction = {
  confirm(chapterCount: number): PermanentChange {
    return {
      action: 'Add pauses',
      scope: `Inserts a pause between paragraphs and at the end of every chapter (${plural(chapterCount, 'chapter')}).`,
      cost: 'Removing them later means deleting each one.',
      destructive: false,
    };
  },
  command: { type: 'AddPauses' } as const satisfies BookCommand,
};

/**
 * Reread keeps its own title and body, and names the confirmed and skipped steps it clears when
 * any checkpoint row exists. Always red: everything the user made on the Book goes.
 */
export const rereadAction: {
  confirm(checkpoints: ProjectStatusDto['checkpoints']): ConfirmOptions;
  command(projects: ProjectsApi, folder: string): Promise<void>;
} = {
  confirm(checkpoints) {
    const steps =
      Object.keys(checkpoints).length > 0 ? ' Clears your confirmed and skipped steps.' : '';
    return {
      title: 'Reread the book?',
      message:
        'This deletes all book data — attribution, generated audio, and edits — and ' +
        `reprocesses the source file from scratch.${steps} This cannot be undone.`,
      destructive: true,
      confirmLabel: 'Delete and reread',
    };
  },
  command: (projects, folder) => projects.import(folder, true),
};
