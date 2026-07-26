using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;

namespace Read2Me.App.State
{
    /// <summary>
    /// The review dialog's checkbox state over a fixed set of <see cref="BookEditReviewRow"/>,
    /// and the source of the edits Apply sends.
    /// </summary>
    /// <remarks>
    /// Hand-editing moves rows in and out of appliability — typing into a <c>Failed</c> row makes
    /// it sendable, emptying a proposed one makes it not — so selection has to be reconciled on
    /// every edit rather than set once. Edits therefore go through <see cref="SetValue"/> and
    /// <see cref="Revert"/> here instead of straight onto the row, which is what keeps
    /// "N selected" and the applied payload the same set.
    /// </remarks>
    public sealed class BookEditReviewSelection(IReadOnlyList<BookEditReviewRow> rows)
    {
        private readonly HashSet<Guid> _selected =
            rows.Where(r => r.IsAppliable).Select(r => r.Proposal.Id).ToHashSet();

        public IReadOnlyList<BookEditReviewRow> Rows { get; } = rows;

        /// <summary>Rows that will be applied: selected and still appliable.</summary>
        public int Count => Rows.Count(IsApplying);

        /// <summary>Rows whose checkbox is enabled.</summary>
        public int SelectableCount => Rows.Count(r => r.IsAppliable);

        /// <summary>Rows carrying a hand edit — what a "discard your edits?" confirm counts.</summary>
        public int HandEditedCount => Rows.Count(r => r.IsUserEdited);

        public bool IsSelected(BookEditReviewRow row) => _selected.Contains(row.Proposal.Id);

        public void Set(BookEditReviewRow row, bool selected)
        {
            if (selected && row.IsAppliable) _selected.Add(row.Proposal.Id);
            else if (!selected) _selected.Remove(row.Proposal.Id);
        }

        /// <summary>Applies a hand edit and re-reconciles the row's selection.</summary>
        public void SetValue(BookEditReviewRow row, string? value) =>
            Edit(row, () => row.UserValue = value);

        /// <summary>Drops a hand edit, restoring the AI's proposal, and re-reconciles.</summary>
        public void Revert(BookEditReviewRow row) => Edit(row, row.Revert);

        /// <summary>
        /// Takes a per-row retry result onto the row and reconciles its selection: a usable answer
        /// is ticked, an unusable one unticked.
        /// </summary>
        /// <remarks>
        /// Unlike a hand edit, this ticks even a row the user had unticked — asking the AI again is
        /// a deliberate act on that row, so a usable answer is wanted. The old id is dropped and
        /// the new one added, so it holds even if a retry ever returns a different target's id.
        /// </remarks>
        public void ReplaceProposal(BookEditReviewRow row, ProposedEdit proposal)
        {
            _selected.Remove(row.Proposal.Id);
            row.ReplaceProposal(proposal);
            if (row.IsAppliable)
                _selected.Add(row.Proposal.Id);
        }

        public void SelectAll()
        {
            foreach (var row in Rows.Where(r => r.IsAppliable))
                _selected.Add(row.Proposal.Id);
        }

        public void Clear() => _selected.Clear();

        public IReadOnlyList<BookEditItem> ToEditItems() =>
            [.. Rows.Where(IsApplying).Select(r => r.ToEditItem())];

        private bool IsApplying(BookEditReviewRow row) => row.IsAppliable && IsSelected(row);

        private void Edit(BookEditReviewRow row, Action edit)
        {
            var wasAppliable = row.IsAppliable;
            edit();
            if (!row.IsAppliable)
                _selected.Remove(row.Proposal.Id);
            else if (!wasAppliable)
                // A row the AI could not use is now sendable — the user only typed into it
                // because they want it applied, so save them the extra click.
                _selected.Add(row.Proposal.Id);
        }
    }
}
