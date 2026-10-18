import type { NextStep } from '@app/pages/project/next-step';
import type { StepRunner } from '@app/pages/project/step-runner';
import { signal } from '@app/core/signals';

export interface GuideEntry {
  folder: string;
  next: NextStep;
  /** The project shell's runner: the pill acts through the same path as the Overview rows. */
  runner: StepRunner;
}

/**
 * The open project's next step, for the app bar's next-step pill (guidance-primitives spec §4).
 * The project shell publishes it from its store's Pipeline; the app shell sits above that
 * element-provided store, so it only reads here and never fetches status itself.
 */
export class ProjectGuide {
  private readonly _current = signal<GuideEntry | null>(null);

  readonly current = this._current.asReadonly();

  publish(folder: string, next: NextStep, runner: StepRunner): void {
    this._current.set({ folder, next, runner });
  }

  /** Forgets `folder`; a project published since stays. */
  clear(folder: string): void {
    if (this._current()?.folder === folder) this._current.set(null);
  }
}
