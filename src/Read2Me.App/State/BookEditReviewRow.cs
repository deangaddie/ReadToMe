using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;

namespace Read2Me.App.State
{
    /// <summary>
    /// What the review dialog shows for one row: the AI's own verdict, or the user's if they
    /// have typed over it.
    /// </summary>
    public enum ReviewRowState
    {
        /// <summary>The AI proposed a change and nobody has touched it.</summary>
        Proposed,

        /// <summary>The value on offer equals the current text — nothing to apply.</summary>
        NoChange,

        /// <summary>The AI produced no value for this target.</summary>
        Failed,

        /// <summary>The user typed a value of their own, and it is appliable.</summary>
        Edited,

        /// <summary>The user emptied the value. V1 has no deletes, so this cannot be applied.</summary>
        Invalid,
    }

    /// <summary>
    /// Mutable view model for one row of the "Edit with AI" review screen: a
    /// <see cref="ProposedEdit"/> plus the user's override of it.
    /// </summary>
    /// <remarks>
    /// The AI reliably finds the right instances but sometimes proposes the wrong fix, so every
    /// row it found is hand-editable — including <see cref="ProposalStatus.NoChange"/> and
    /// <see cref="ProposalStatus.Failed"/> rows, which become appliable once the user types into
    /// them. Overrides live for the lifetime of the open dialog only; nothing here is persisted.
    /// </remarks>
    public sealed class BookEditReviewRow(ProposedEdit proposal)
    {
        public ProposedEdit Proposal { get; private set; } = proposal;

        /// <summary>
        /// The user's value, or null when they have not typed into this row. Setting it to the
        /// AI's own value still counts as a hand edit — the mark tracks ownership, not difference.
        /// </summary>
        public string? UserValue { get; set; }

        public bool IsUserEdited => UserValue is not null;

        public string? EffectiveValue => UserValue ?? Proposal.NewValue;

        public ReviewRowState State =>
            UserValue switch
            {
                null => Proposal.Status switch
                {
                    ProposalStatus.Proposed => ReviewRowState.Proposed,
                    ProposalStatus.NoChange => ReviewRowState.NoChange,
                    ProposalStatus.Failed => ReviewRowState.Failed,
                    // Mirrored, not defaulted: a new ProposalStatus should stop here loudly rather
                    // than quietly render as one of the states above.
                    var s => throw new NotSupportedException($"Unmapped proposal status {s}."),
                },
                var v when string.IsNullOrWhiteSpace(v) => ReviewRowState.Invalid,
                var v when v == Proposal.OldValue => ReviewRowState.NoChange,
                _ => ReviewRowState.Edited,
            };

        public bool IsAppliable => State is ReviewRowState.Proposed or ReviewRowState.Edited;

        /// <summary>Drops the hand edit, restoring the AI's proposal.</summary>
        public void Revert() => UserValue = null;

        /// <summary>
        /// Takes a fresh proposal — from a per-row retry — and clears any pending hand edit.
        /// Retry wins; the user can edit the new proposal afterwards.
        /// </summary>
        public void ReplaceProposal(ProposedEdit proposal)
        {
            Proposal = proposal;
            UserValue = null;
        }

        public BookEditItem ToEditItem() =>
            IsAppliable
                ? new BookEditItem(Proposal.Kind, Proposal.Id, EffectiveValue!)
                : throw new InvalidOperationException(
                    $"Row {Proposal.DisplayPath} is {State} and cannot be applied.");
    }
}
