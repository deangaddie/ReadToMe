import type { BookEditRow, ProposalStatus } from '@app/api';
import { ReviewSelection, effectiveValue, isUserEdited, rowState } from './review-model';

/**
 * Ported from `Read2Me.Tests/State/BookEditReviewSelectionTests.cs` — the Blazor dialog and this
 * one have to agree on what "N selected" means and what Apply sends.
 */
function proposal(
  status: ProposalStatus,
  oldValue = 'Chapter I',
  newValue: string | null = 'Chapter 1',
): BookEditRow {
  return {
    kind: 'ChapterTitle',
    id: crypto.randomUUID(),
    displayPath: `Book / ${oldValue}`,
    oldValue,
    newValue: status === 'Failed' ? null : newValue,
    status,
    failureReason: status === 'Failed' ? 'model returned nothing' : null,
  };
}

/** What a retry hands back for a row that already exists. */
function retryResult(
  existing: BookEditRow,
  status: ProposalStatus,
  newValue: string | null,
): BookEditRow {
  return {
    ...existing,
    newValue: status === 'Failed' ? null : newValue,
    status,
    failureReason: status === 'Failed' ? 'model returned nothing' : null,
  };
}

function selectionOf(...proposals: BookEditRow[]): ReviewSelection {
  return ReviewSelection.from(proposals);
}

/** The row for a proposal, read out of the current (immutable) selection. */
function row(selection: ReviewSelection, proposal: BookEditRow) {
  const found = selection.rows.find((r) => r.proposal.id === proposal.id);
  if (!found) throw new Error(`no row for ${proposal.id}`);
  return found;
}

describe('row state', () => {
  it('mirrors the AI verdict while the user has not typed', () => {
    expect(rowState({ proposal: proposal('Proposed'), userValue: null })).toBe('proposed');
    expect(rowState({ proposal: proposal('NoChange'), userValue: null })).toBe('noChange');
    expect(rowState({ proposal: proposal('Failed'), userValue: null })).toBe('failed');
  });

  it('reads a hand edit as the user’s verdict, blank as invalid and the old text as no change', () => {
    const p = proposal('Proposed');
    expect(rowState({ proposal: p, userValue: 'Chapter One' })).toBe('edited');
    expect(rowState({ proposal: p, userValue: '  ' })).toBe('invalid');
    expect(rowState({ proposal: p, userValue: 'Chapter I' })).toBe('noChange');
  });

  it('counts a value typed over the AI as a hand edit even when it matches', () => {
    const p = proposal('Proposed');
    expect(isUserEdited({ proposal: p, userValue: 'Chapter 1' })).toBe(true);
    expect(effectiveValue({ proposal: p, userValue: null })).toBe('Chapter 1');
  });
});

describe('ReviewSelection', () => {
  it('ticks every appliable row on creation', () => {
    const proposed = proposal('Proposed');
    const noChange = proposal('NoChange');
    const failed = proposal('Failed');

    const selection = selectionOf(proposed, noChange, failed);

    expect(selection.isSelected(row(selection, proposed))).toBe(true);
    expect(selection.isSelected(row(selection, noChange))).toBe(false);
    expect(selection.isSelected(row(selection, failed))).toBe(false);
    expect(selection.count).toBe(1);
    expect(selection.selectableCount).toBe(1);
  });

  it('refuses to tick a row that cannot be applied', () => {
    const failed = proposal('Failed');
    let selection = selectionOf(failed);

    selection = selection.set(row(selection, failed), true);

    expect(selection.isSelected(row(selection, failed))).toBe(false);
  });

  it('unticks on request', () => {
    const p = proposal('Proposed');
    let selection = selectionOf(p);

    selection = selection.set(row(selection, p), false);

    expect(selection.isSelected(row(selection, p))).toBe(false);
    expect(selection.count).toBe(0);
  });

  it('auto-ticks a failed row the user has typed into', () => {
    const failed = proposal('Failed');
    let selection = selectionOf(failed);

    selection = selection.setValue(row(selection, failed), 'Chapter One');

    expect(selection.isSelected(row(selection, failed))).toBe(true);
    expect(selection.count).toBe(1);
    expect(selection.selectableCount).toBe(1);
  });

  it('does not re-tick a row the user deliberately unticked', () => {
    const p = proposal('Proposed');
    let selection = selectionOf(p);
    selection = selection.set(row(selection, p), false);

    selection = selection.setValue(row(selection, p), 'Chapter One');

    expect(selection.isSelected(row(selection, p))).toBe(false);
  });

  it('unticks a row edited into an empty value', () => {
    const p = proposal('Proposed');
    let selection = selectionOf(p);

    selection = selection.setValue(row(selection, p), '  ');

    expect(selection.isSelected(row(selection, p))).toBe(false);
    expect(selection.count).toBe(0);
    expect(selection.selectableCount).toBe(0);
  });

  it('unticks a row edited back to the current text', () => {
    const p = proposal('Proposed');
    let selection = selectionOf(p);

    selection = selection.setValue(row(selection, p), 'Chapter I');

    expect(selection.isSelected(row(selection, p))).toBe(false);
  });

  it('drops the selection when an edited failed row is reverted', () => {
    const failed = proposal('Failed');
    let selection = selectionOf(failed);
    selection = selection.setValue(row(selection, failed), 'Chapter One');

    selection = selection.revert(row(selection, failed));

    expect(isUserEdited(row(selection, failed))).toBe(false);
    expect(selection.isSelected(row(selection, failed))).toBe(false);
  });

  it('selects all appliable rows including hand-edited ones', () => {
    const proposed = proposal('Proposed');
    const failed = proposal('Failed');
    let selection = selectionOf(proposed, failed).clear();
    selection = selection.setValue(row(selection, failed), 'Chapter One');
    selection = selection.set(row(selection, failed), false);

    selection = selection.selectAll();

    expect(selection.isSelected(row(selection, proposed))).toBe(true);
    expect(selection.isSelected(row(selection, failed))).toBe(true);
    expect(selection.count).toBe(2);
  });

  it('clears everything', () => {
    const selection = selectionOf(proposal('Proposed'), proposal('Proposed')).clear();

    expect(selection.count).toBe(0);
  });

  it('sends only the ticked appliable rows, with their effective values', () => {
    const proposed = proposal('Proposed');
    const deselected = proposal('Proposed');
    const edited = proposal('Failed');
    const untouchedFailure = proposal('Failed');
    let selection = selectionOf(proposed, deselected, edited, untouchedFailure);
    selection = selection.set(row(selection, deselected), false);
    selection = selection.setValue(row(selection, edited), 'Chapter One');

    const edits = selection.toEdits();

    expect(edits.map((e) => e.id)).toEqual([proposed.id, edited.id]);
    expect(edits.map((e) => e.newValue)).toEqual(['Chapter 1', 'Chapter One']);
    expect(edits.map((e) => e.kind)).toEqual(['ChapterTitle', 'ChapterTitle']);
  });

  it('never promises more edits than it sends', () => {
    // Apply's count and Apply's payload read the same rows.
    const p = proposal('Proposed');
    let selection = selectionOf(p);

    selection = selection.setValue(row(selection, p), '  ');

    expect(selection.count).toBe(0);
    expect(selection.toEdits()).toEqual([]);
  });

  it('ticks a row whose retry came back usable, and drops its hand edit', () => {
    // The user asked for this row again, so a usable answer is wanted — even if they had unticked
    // it, or typed over the answer the retry just replaced.
    const failed = proposal('Failed');
    let selection = selectionOf(failed);
    selection = selection.setValue(row(selection, failed), 'Chapter One');
    selection = selection.set(row(selection, failed), false);

    selection = selection.replaceProposal(
      row(selection, failed),
      retryResult(failed, 'Proposed', 'Chapter 1'),
    );

    expect(isUserEdited(row(selection, failed))).toBe(false);
    expect(effectiveValue(row(selection, failed))).toBe('Chapter 1');
    expect(selection.isSelected(row(selection, failed))).toBe(true);
    expect(selection.count).toBe(1);
  });

  it.each(['Failed', 'NoChange'] as const)(
    'unticks a row whose retry came back %s',
    (status: ProposalStatus) => {
      const p = proposal('Proposed');
      let selection = selectionOf(p);

      selection = selection.replaceProposal(row(selection, p), retryResult(p, status, 'Chapter I'));

      expect(selection.isSelected(row(selection, p))).toBe(false);
      expect(selection.count).toBe(0);
    },
  );

  it('counts the rows a discard confirm would throw away', () => {
    const edited = proposal('Proposed');
    const reverted = proposal('Proposed');
    let selection = selectionOf(edited, reverted, proposal('Failed'));
    selection = selection.setValue(row(selection, edited), 'Chapter One');
    selection = selection.setValue(row(selection, reverted), 'Chapter Two');
    selection = selection.revert(row(selection, reverted));

    expect(selection.handEditedCount).toBe(1);
  });

  it('leaves the selection it was called on untouched', () => {
    // The component holds the selection in a signal and replaces it; a mutation in place would
    // update the rows behind a template that had already rendered them.
    const p = proposal('Proposed');
    const original = selectionOf(p);

    const edited = original.setValue(row(original, p), 'Chapter One');

    expect(isUserEdited(row(original, p))).toBe(false);
    expect(isUserEdited(row(edited, p))).toBe(true);
  });
});
