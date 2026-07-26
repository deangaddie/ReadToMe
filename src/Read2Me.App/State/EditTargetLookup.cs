using Read2Me.Services.BookEdits;

namespace Read2Me.App.State
{
    /// <summary>
    /// Maps a review row back to the <see cref="EditTarget"/> it was proposed from, which is what
    /// a per-row "ask the AI again" needs to re-run a single target.
    /// </summary>
    public static class EditTargetLookup
    {
        /// <summary>
        /// The target behind <paramref name="row"/>, or null if the plan's targets no longer
        /// contain it.
        /// </summary>
        public static EditTarget? FindTarget(
            this IReadOnlyList<EditTarget> targets, BookEditReviewRow row) =>
            // Ids are per-entity, so kind is part of identity here: a chapter and one of its
            // paragraphs are different targets that could carry the same Guid.
            targets.FirstOrDefault(t => t.Id == row.Proposal.Id && t.Kind == row.Proposal.Kind);
    }
}
