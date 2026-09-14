import { Injectable, computed, signal } from '@angular/core';
import { NodeLevel, ParagraphRefDto } from '@app/api';
import { ParagraphAncestry, SelectionMap, TriState, ancestryOf, nodeState } from './selection';

/**
 * The reader's paragraph selection (ticket 12, design §6.3), provided by the project shell so it
 * survives child-route changes. Holds selected paragraph ids with the nodes each rolls up into,
 * and the totals it has learnt per node (from a `paragraph-ids` read, or from a loaded chapter)
 * so a tree checkbox can say "all of them". The rules are in `selection.ts`.
 */
@Injectable()
export class SelectionStore {
  private readonly _selection = signal<SelectionMap>({});
  private readonly _totals = signal<Readonly<Record<string, number>>>({});

  readonly selection = this._selection.asReadonly();
  readonly ids = computed(() => Object.keys(this._selection()));
  readonly count = computed(() => this.ids().length);
  /** The ids as a set, for a row's "am I selected" without scanning. */
  readonly selected = computed<ReadonlySet<string>>(() => new Set(this.ids()));
  /** Character paragraphs per node, as far as known. */
  readonly totals = this._totals.asReadonly();

  has(id: string): boolean {
    return id in this._selection();
  }

  toggle(id: string, ancestry: ParagraphAncestry, on: boolean): void {
    this._selection.update((s) => {
      if (on) return { ...s, [id]: ancestry };
      if (!(id in s)) return s;
      const next = { ...s };
      delete next[id];
      return next;
    });
  }

  /** Adds every ref (a node's paragraphs); an already selected one keeps its place. */
  add(refs: readonly ParagraphRefDto[]): void {
    if (refs.length === 0) return;
    this._selection.update((s) => {
      const next = { ...s };
      for (const ref of refs) next[ref.id] = ancestryOf(ref);
      return next;
    });
  }

  remove(ids: readonly string[]): void {
    if (ids.length === 0) return;
    this._selection.update((s) => {
      const next = { ...s };
      for (const id of ids) delete next[id];
      return next;
    });
  }

  clear(): void {
    if (this.count() > 0) this._selection.set({});
  }

  /** A node's Character-paragraph total became known (a full `paragraph-ids` read, a loaded chapter). */
  learnTotals(totals: Readonly<Record<string, number>>): void {
    this._totals.update((t) => ({ ...t, ...totals }));
  }

  nodeState(level: NodeLevel, nodeId: string): TriState {
    return nodeState(this._selection(), level, nodeId, this._totals()[nodeId]);
  }

  /** Everything forgotten — the shell leaving the project. */
  reset(): void {
    this._selection.set({});
    this._totals.set({});
  }
}
