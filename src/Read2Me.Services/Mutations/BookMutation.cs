using Read2Me.Core.Models;

namespace Read2Me.Services.Mutations;

/// <summary>
/// One user or domain operation against a Book, even when it changes several records.
/// A mutation is a request, not an effect: what it actually changed is reported by the
/// implementation that applies it (<see cref="BookMutationEffects"/>) and carried to readers
/// on a <see cref="BookMutationReceipt"/>.
/// <para>
/// This is the write-side sibling of <see cref="BookCommand"/>. During the migration described by
/// ADR 0007 both exist: <see cref="BookCommand"/> remains the wire and legacy-caller shape, and
/// each family that migrates gains a mutation here. The legacy façade is deleted once no caller
/// remains.
/// </para>
/// </summary>
public abstract record BookMutation(ProjectFolderId FolderId)
{
    /// <summary>The mutation's identity for receipts and logs — its type name.</summary>
    public string Name => GetType().Name;
}

/// <summary>
/// Creates one Speech ParagraphItem next to <c>AnchorItemId</c>, inside the anchor's own Paragraph.
/// The new item is born unattributed; see <see cref="InsertParagraphItemCommand"/> for why.
/// </summary>
public sealed record InsertParagraphItemMutation(
    ProjectFolderId FolderId,
    Guid AnchorItemId,
    InsertPosition Position,
    string Text) : BookMutation(FolderId);
