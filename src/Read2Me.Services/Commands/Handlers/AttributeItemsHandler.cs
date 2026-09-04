using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The Character Queue's answer, migrated to <see cref="BookMutations"/> (ADR 0007). The stamping
/// rules, and the exact Paragraph and ParagraphItems a Book View refreshes from, live in
/// <see cref="Mutations.Implementations.AttributeParagraphItemsMutationImplementation"/>; this
/// handler stays registered so <c>POST /api/projects/{folder}/commands</c> keeps its existing
/// request and response shape.
/// <para>
/// It no longer publishes a reconciliation event of its own. Open Book Views converge on the
/// committed receipt like every other producer's, which is what lets a queue run reach a second
/// circuit at all.
/// </para>
/// <para>
/// One behavioural change comes with that: an answer whose stamps all agree with what the items
/// already carry is now <c>NoChange</c> rather than a save. It consumes no revision, so a re-run
/// over an attributed chapter no longer makes every open Book View reread it per paragraph. The
/// response stays <c>null</c> either way.
/// </para>
/// </summary>
public sealed class AttributeItemsHandler(BookMutations mutations) : ICommandHandler<AttributeItemsCommand>
{
    public Task<Guid?> HandleAsync(AttributeItemsCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new AttributeParagraphItemsMutation(c.FolderId, c.ParagraphId, c.Items), ct);
}
