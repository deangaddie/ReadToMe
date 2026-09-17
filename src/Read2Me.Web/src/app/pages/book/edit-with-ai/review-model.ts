import type { BookEditItem, BookEditPlanStatus, BookEditRow, Guid } from '@app/api';

/**
 * What the review screen shows for one row: the AI's own verdict, or the user's if they have typed
 * over it. Mirrors `ReviewRowState` in `Read2Me.App/State/BookEditReviewRow.cs`.
 */
export type ReviewRowState =
  /** The AI proposed a change and nobody has touched it. */
  | 'proposed'
  /** The value on offer equals the current text — nothing to apply. */
  | 'noChange'
  /** The AI produced no value for this target. */
  | 'failed'
  /** The user typed a value of their own, and it is appliable. */
  | 'edited'
  /** The user emptied the value. V1 has no deletes, so this cannot be applied. */
  | 'invalid';

/**
 * One row of the review screen: a proposal plus the user's override of it. Overrides live for the
 * lifetime of the open dialog only; nothing here is persisted.
 */
export interface ReviewRow {
  readonly proposal: BookEditRow;
  /**
   * The user's value, or null when they have not typed into this row. Setting it to the AI's own
   * value still counts as a hand edit — the mark tracks ownership, not difference.
   */
  readonly userValue: string | null;
}

export function isUserEdited(row: ReviewRow): boolean {
  return row.userValue !== null;
}

export function effectiveValue(row: ReviewRow): string | null {
  return row.userValue ?? row.proposal.newValue ?? null;
}

export function rowState(row: ReviewRow): ReviewRowState {
  if (row.userValue === null) {
    switch (row.proposal.status) {
      case 'Proposed':
        return 'proposed';
      case 'NoChange':
        return 'noChange';
      case 'Failed':
        return 'failed';
    }
  }
  if (row.userValue.trim() === '') return 'invalid';
  if (row.userValue === row.proposal.oldValue) return 'noChange';
  return 'edited';
}

export function isAppliable(row: ReviewRow): boolean {
  const state = rowState(row);
  return state === 'proposed' || state === 'edited';
}

/**
 * The review screen's checkbox state over a fixed set of rows, and the source of the edits Apply
 * sends. Ported from `BookEditReviewSelection.cs`.
 *
 * Hand-editing moves rows in and out of appliability — typing into a failed row makes it sendable,
 * emptying a proposed one makes it not — so selection is reconciled on every edit rather than set
 * once. Edits therefore go through {@link setValue} and {@link revert} here instead of straight
 * onto the row, which is what keeps "N selected" and the applied payload the same set.
 *
 * Every mutator answers a new selection, so the component can hold one in a signal.
 */
export class ReviewSelection {
  private constructor(
    readonly rows: readonly ReviewRow[],
    private readonly selected: ReadonlySet<Guid>,
  ) {}

  /** A fresh review: every appliable row starts ticked. */
  static from(proposals: readonly BookEditRow[]): ReviewSelection {
    const rows = proposals.map((proposal) => ({ proposal, userValue: null }));
    // A program has one target selector, so an id names at most one row within a session.
    return new ReviewSelection(
      rows,
      new Set(rows.filter(isAppliable).map((r) => r.proposal.id)),
    );
  }

  static empty(): ReviewSelection {
    return new ReviewSelection([], new Set());
  }

  /** Rows that will be applied: ticked and still appliable. */
  get count(): number {
    return this.rows.filter((r) => this.isApplying(r)).length;
  }

  /** Rows whose checkbox is enabled. */
  get selectableCount(): number {
    return this.rows.filter(isAppliable).length;
  }

  /** Rows carrying a hand edit — what a "discard your edits?" confirm counts. */
  get handEditedCount(): number {
    return this.rows.filter(isUserEdited).length;
  }

  isSelected(row: ReviewRow): boolean {
    return this.selected.has(row.proposal.id);
  }

  set(row: ReviewRow, selected: boolean): ReviewSelection {
    if (selected && !isAppliable(row)) return this;
    return new ReviewSelection(this.rows, this.withSelected(row.proposal.id, selected));
  }

  /** Applies a hand edit and re-reconciles the row's selection. */
  setValue(row: ReviewRow, value: string | null): ReviewSelection {
    return this.edit(row, { ...row, userValue: value });
  }

  /** Drops a hand edit, restoring the AI's proposal, and re-reconciles. */
  revert(row: ReviewRow): ReviewSelection {
    return this.edit(row, { ...row, userValue: null });
  }

  /**
   * Takes a per-row retry result onto the row and reconciles its selection: a usable answer is
   * ticked, an unusable one unticked.
   *
   * Unlike a hand edit, this ticks even a row the user had unticked — asking the AI again is a
   * deliberate act on that row, so a usable answer is wanted. The hand edit goes: retry wins.
   */
  replaceProposal(row: ReviewRow, proposal: BookEditRow): ReviewSelection {
    const next: ReviewRow = { proposal, userValue: null };
    const selected = new Set(this.selected);
    selected.delete(row.proposal.id);
    if (isAppliable(next)) selected.add(proposal.id);
    return new ReviewSelection(this.replace(row, next), selected);
  }

  selectAll(): ReviewSelection {
    const selected = new Set(this.selected);
    for (const row of this.rows.filter(isAppliable)) selected.add(row.proposal.id);
    return new ReviewSelection(this.rows, selected);
  }

  clear(): ReviewSelection {
    return new ReviewSelection(this.rows, new Set());
  }

  toEdits(): BookEditItem[] {
    return this.rows
      .filter((r) => this.isApplying(r))
      .map((r) => ({ kind: r.proposal.kind, id: r.proposal.id, newValue: effectiveValue(r) ?? '' }));
  }

  private isApplying(row: ReviewRow): boolean {
    return isAppliable(row) && this.isSelected(row);
  }

  private edit(row: ReviewRow, next: ReviewRow): ReviewSelection {
    const wasAppliable = isAppliable(row);
    const selected = new Set(this.selected);
    if (!isAppliable(next)) {
      selected.delete(next.proposal.id);
    } else if (!wasAppliable) {
      // A row the AI could not use is now sendable — the user only typed into it because they
      // want it applied, so save them the extra click.
      selected.add(next.proposal.id);
    }
    return new ReviewSelection(this.replace(row, next), selected);
  }

  private replace(row: ReviewRow, next: ReviewRow): ReviewRow[] {
    return this.rows.map((r) => (r.proposal.id === row.proposal.id ? next : r));
  }

  private withSelected(id: Guid, selected: boolean): Set<Guid> {
    const next = new Set(this.selected);
    if (selected) next.add(id);
    else next.delete(id);
    return next;
  }
}

/**
 * What the Instruct screen says when a plan did not produce a program. `Ok` never reaches here.
 * Mirrors the Blazor dialog's status mapping so both UIs explain a refusal the same way.
 */
export function planErrorMessage(status: BookEditPlanStatus, reason: string | null): string {
  switch (status) {
    case 'NoLlmConfigured':
      return 'No LLM server is configured. Set one up under Settings → LLM.';
    case 'Unsupported':
      return `This instruction can't be applied: ${reason ?? ''} AI edits can change titles and paragraph text only.`;
    case 'ServiceUnavailable':
      return `The LLM service is unavailable: ${reason ?? ''}`;
    case 'NoTargets':
      return 'No items in the book match this instruction’s scope. Try rephrasing.';
    default:
      return `Could not understand the instruction: ${reason ?? ''}`;
  }
}
