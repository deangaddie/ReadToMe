import { signal } from '@app/core/signals';
import type { HistoryStore } from './history-store';

export interface HistoryEntry {
  folder: string;
  store: HistoryStore;
}

/**
 * Whether the History sheet is open, and the open project's {@link HistoryStore}. The project shell
 * publishes its store here; the app shell sits above that element-provided store, so it reads it
 * here for the app-bar button and the sheet beside the page. The sheet stays open across a
 * project's pages.
 */
export class HistorySheetState {
  private readonly _open = signal(false);
  private readonly _current = signal<HistoryEntry | null>(null);

  readonly open = this._open.asReadonly();
  readonly current = this._current.asReadonly();

  toggle(): void {
    this._open.update((open) => !open);
  }

  close(): void {
    this._open.set(false);
  }

  publish(folder: string, store: HistoryStore): void {
    this._current.set({ folder, store });
  }

  /** Forgets `folder`; a project published since stays. */
  clear(folder: string): void {
    if (this._current()?.folder === folder) this._current.set(null);
  }
}
