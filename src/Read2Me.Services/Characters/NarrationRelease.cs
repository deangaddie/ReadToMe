using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Characters;

/// <summary>
/// The one predicate behind Release narration: the narrator-stamped speech items of a scope's
/// narration-only paragraphs, meaning paragraphs with no dialog item at all. The write
/// (<c>ReleaseNarrationMutationImplementation</c>) and the confirm's preview
/// (<c>GetNarrationReleasePreviewAsync</c>) both ask through here, so the count a producer confirms
/// is the count that is written.
/// <para>
/// A speech tag inside a quoted paragraph ("he said") is narration in a paragraph that holds dialog,
/// so it is never releasable. Pauses are nobody's, so they never match either.
/// </para>
/// </summary>
public static class NarrationRelease
{
    /// <summary>
    /// The wire binds enums from integers too, so a scope the enum does not name can arrive; callers
    /// refuse it first with this, and <see cref="ReleasableItems"/> treats it as a bug.
    /// </summary>
    public static bool IsKnownScope(NarrationReleaseScope scope) => Enum.IsDefined(scope);

    public static IQueryable<ParagraphItem> ReleasableItems(
        ProjectDbContext db, NarrationReleaseScope scope, Guid nodeId)
    {
        var inScope = scope switch
        {
            NarrationReleaseScope.Part      => db.ParagraphItems.Where(i => i.Paragraph.Chapter.PartId == nodeId),
            NarrationReleaseScope.Chapter   => db.ParagraphItems.Where(i => i.Paragraph.ChapterId == nodeId),
            NarrationReleaseScope.Paragraph => db.ParagraphItems.Where(i => i.ParagraphId == nodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown release scope."),
        };

        var paragraphsWithDialog = db.ParagraphItems
            .Where(NarrationRule.IsDialogExpression)
            .Select(i => i.ParagraphId);

        return inScope
            .Where(ParagraphItemKinds.IsSpeechExpression)
            .Where(NarrationRule.IsNarrationExpression)
            .Where(i => !paragraphsWithDialog.Contains(i.ParagraphId));
    }
}
