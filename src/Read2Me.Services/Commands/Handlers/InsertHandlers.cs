using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Creates one Speech ParagraphItem beside an anchor item, inside the anchor's own Paragraph —
/// the repair for an item the import-time split merged across two speakers.
/// <para>
/// The handler is only a translation (ADR 0007): the write itself, its transaction, its commit
/// point and its receipt live in
/// <see cref="Mutations.Implementations.InsertParagraphItemMutationImplementation"/>. It stays
/// registered so <c>POST /api/projects/{folder}/commands</c> keeps its existing request and
/// response shape.
/// </para>
/// </summary>
public sealed class InsertParagraphItemHandler(BookMutations mutations) : ICommandHandler<InsertParagraphItemCommand>
{
    public Task<BookCommandResult> HandleAsync(InsertParagraphItemCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new InsertParagraphItemMutation(c.FolderId, c.AnchorItemId, c.Position, c.Text), ct);
}

/// <summary>
/// Creates one node beside or inside an anchor (Node insertion). A translation only (ADR 0007): the
/// write lives in <see cref="Mutations.Implementations.InsertNodeMutationImplementation"/>. Strict
/// on the wire — an agent scripting a structural repair is told about an unknown anchor rather than
/// handed a quiet null it could mistake for a no-op.
/// </summary>
public sealed class InsertNodeHandler(BookMutations mutations) : ICommandHandler<InsertNodeCommand>
{
    public Task<BookCommandResult> HandleAsync(InsertNodeCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandStrictAsync(
            new InsertNodeMutation(c.FolderId, c.AnchorKind, c.AnchorId, c.Placement, c.Title, c.Text), ct);
}
