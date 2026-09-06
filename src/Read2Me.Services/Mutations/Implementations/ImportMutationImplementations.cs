using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Replacing a Book's content — the initial import, the automatic reread, and the manual reread
/// (ADR 0007).
/// <para>
/// The removal and the repopulation are one <see cref="BookMutations.CommitAsync"/> call, so they
/// are one transaction, one revision and one receipt. Nothing outside this method ever observes the
/// Book between the two: a second open Book View converging on the receipt reads the imported Book,
/// never the empty one, and a failure part-way through leaves the Book exactly as it was.
/// </para>
/// </summary>
public sealed class ImportBookContentMutationImplementation(IBookContentPersister persister)
    : IBookMutationImplementation<ImportBookContentMutation>
{
    /// <summary>
    /// What replacing content changes. Every item-scoped facet moves because the items themselves
    /// do: a reread takes each one's text, speaker, audio and review with it, and the import puts
    /// back items that have none of them.
    /// </summary>
    private const BookFacets ReplacedFacets =
        BookFacets.Structure | BookFacets.ItemText | BookFacets.Attribution
        | BookFacets.Audio | BookFacets.Reviews;

    public async Task<BookMutationEffects> ApplyAsync(
        ImportBookContentMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No project record found in '{mutation.FolderId.Value}'.");

        var removed = mutation.ReplaceExisting ? await BookContentRemoval.ClearAsync(db, ct) : 0;

        var added = await persister.PersistAsync(db, mutation.Content, ct);

        // Only while the project has none: an import must not overwrite a cover the reader chose,
        // and the producer checked the same thing before staging the file. Checking again here is
        // what makes the answer the transaction's rather than a read taken moments earlier.
        var coverNamed = mutation.CoverImageFileName is { Length: > 0 } cover && project.CoverImage is null;
        if (coverNamed) project.CoverImage = mutation.CoverImageFileName;

        var facets = BookFacets.None;
        if (removed > 0 || added > 0) facets |= ReplacedFacets;
        // The cover is not Book content, but it is on the project row a Book View reads, so a commit
        // that only renamed it still has to reach open readers.
        if (coverNamed) facets |= BookFacets.ProjectPolicy;

        return facets == BookFacets.None
            ? BookMutationEffects.Nothing
            // Nothing here is named: a replacement creates every node in the Book, so enumerating
            // them would be an inventory rather than a hint, and every reader rebuilds regardless.
            : new BookMutationEffects { Scope = BookMutationScope.WholeProject, Facets = facets };
    }
}
