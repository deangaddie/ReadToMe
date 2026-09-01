using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Creates one Speech ParagraphItem beside an anchor item, inside the anchor's own Paragraph —
/// the repair for an item the import-time split merged across two speakers.
/// <para>
/// The ordering lives in <see cref="Books.BookHierarchy.PlanInsertParagraphItem"/> beside the merge
/// and split logic that reasons over the same sibling list, and is applied by
/// <see cref="BookMutationApplier"/>. The new item is born unattributed, which is the point of the
/// feature rather than an omission: the anchor held two speakers, so its speaker is usually not the
/// new item's, and inheriting it would look attributed while never reaching the attribution queue.
/// </para>
/// <para>
/// The whitespace guard is here and not only in the dialog:
/// <c>POST /api/projects/{folder}/commands</c> resolves any <see cref="BookCommand"/> by name, so an
/// agent can post this command with no dialog in front of it. The throw surfaces there as a 422.
/// </para>
/// </summary>
public sealed class InsertParagraphItemHandler(ProjectDbSession session) : ICommandHandler<InsertParagraphItemCommand>
{
    public async Task<Guid?> HandleAsync(InsertParagraphItemCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Text))
            throw new InvalidOperationException("An inserted item needs text — whitespace alone is not an item.");

        var db = await session.OpenAsync(c.FolderId);
        var mutation = await BookMutationApplier.PlanAndApplyAsync(
            db, h => h.PlanInsertParagraphItem(c.AnchorItemId, c.Position, c.Text));
        return mutation != null ? ((ParagraphItem)mutation.ToAdd[0]).Id : null;
    }
}
