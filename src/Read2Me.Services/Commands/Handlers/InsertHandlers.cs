using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Creates one Speech ParagraphItem beside an anchor item, inside the anchor's own Paragraph —
/// the repair for an item the import-time split merged across two speakers.
/// <para>
/// This is the first family migrated to <see cref="BookMutations"/> (ADR 0007), so the handler is
/// now only a translation: the write itself, its transaction, its commit point and its receipt live
/// in <see cref="Mutations.Implementations.InsertParagraphItemMutationImplementation"/>. The handler
/// stays registered so that callers still holding <see cref="IBookCommandHandler"/> — the commands
/// endpoint and the Book View menu — keep working unchanged during the migration.
/// </para>
/// </summary>
public sealed class InsertParagraphItemHandler(BookMutations mutations) : ICommandHandler<InsertParagraphItemCommand>
{
    public Task<Guid?> HandleAsync(InsertParagraphItemCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new InsertParagraphItemMutation(c.FolderId, c.AnchorItemId, c.Position, c.Text), ct);
}
