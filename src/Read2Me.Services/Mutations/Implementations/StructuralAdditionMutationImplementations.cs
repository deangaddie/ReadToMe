using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// The bulk structural additions — book, volume, part and chapter titles, and the implied pauses.
/// Each sweeps the whole Book, adding Paragraphs wherever its rule applies, so none of them can
/// name what it touched: the scope is whole-project and a reader rebuilds.
/// <para>
/// A sweep that finds nothing to add returns <see cref="BookMutationEffects.Nothing"/>, which is
/// the difference the legacy handlers could not express — running "Add Pauses" twice used to
/// commit, bump nothing and still report success.
/// </para>
/// </summary>
internal static class StructuralAdditionEffects
{
    public static BookMutationEffects Swept { get; } = new()
    {
        Scope = BookMutationScope.WholeProject,
        Facets = BookFacets.Structure,
    };

    /// <summary>
    /// Gives each planned node a leading Chapter that speaks its title. Volumes and Parts differ
    /// only in which planner produced the list, so they share the staging.
    /// </summary>
    public static BookMutationEffects StageTitleChapters(
        ProjectDbContext db, List<(Guid NodeId, string Title, Chapter NewChapter, string? FirstChapterOrder)> planned)
    {
        if (planned.Count == 0) return BookMutationEffects.Nothing;

        foreach (var (_, title, chapter, _) in planned)
        {
            db.Chapters.Add(chapter);
            TitleInserter.AddTitleParagraph(db, chapter.Id, title, null);
        }
        return Swept;
    }
}

public sealed class AddBookTitleMutationImplementation : IBookMutationImplementation<AddBookTitleMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddBookTitleMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound, "The project has no record to take a book title from.");

        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        // No volumes yet: there is no front of the Book to put a title at.
        if (hierarchy.PlanFrontMatterInsert() is not { } plan) return BookMutationEffects.Nothing;

        var (structure, chapterId, _) = plan;
        BookMutationApplier.StageMutation(db, structure);
        var title = TitleInserter.AddTitleParagraph(db, chapterId, project.BookTitle, null);
        TitleInserter.AddTitleParagraphAfter(db, chapterId, $"By {project.Author}", title.Order);
        return StructuralAdditionEffects.Swept;
    }
}

public sealed class AddVolumeTitlesMutationImplementation : IBookMutationImplementation<AddVolumeTitlesMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddVolumeTitlesMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        return StructuralAdditionEffects.StageTitleChapters(db, hierarchy.PlanVolumeTitleChapters());
    }
}

public sealed class AddPartTitlesMutationImplementation : IBookMutationImplementation<AddPartTitlesMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddPartTitlesMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        return StructuralAdditionEffects.StageTitleChapters(db, hierarchy.PlanPartTitleChapters());
    }
}

public sealed class AddChapterTitlesMutationImplementation : IBookMutationImplementation<AddChapterTitlesMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddChapterTitlesMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var planned = hierarchy.PlanChapterTitleInsertions();
        if (planned.Count == 0) return BookMutationEffects.Nothing;

        foreach (var (chapterId, title, firstParagraphOrder) in planned)
            TitleInserter.AddTitleParagraph(db, chapterId, title, firstParagraphOrder);
        return StructuralAdditionEffects.Swept;
    }
}

public sealed class AddPausesMutationImplementation : IBookMutationImplementation<AddPausesMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddPausesMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var planned = hierarchy.PlanPauseInsertions();
        // The planner already skips pauses the Book has, so a second run is genuinely a no-op.
        if (planned.Count == 0) return BookMutationEffects.Nothing;

        foreach (var pause in planned)
            PauseInserter.AddPauseParagraph(db, pause.ChapterId, pause.PauseType, pause.AfterOrder, pause.BeforeOrder);
        return StructuralAdditionEffects.Swept;
    }
}

/// <summary>
/// One pause Paragraph beside the anchor item's Paragraph — the only member of this family whose
/// effects are exhaustive, because it knows exactly the Paragraph it made and where.
/// </summary>
public sealed class InsertPauseParagraphMutationImplementation
    : IBookMutationImplementation<InsertPauseParagraphMutation>
{
    private static ParagraphItemType PauseTypeOf(PauseKind kind) => kind switch
    {
        PauseKind.Pause => ParagraphItemType.Pause,
        PauseKind.ParagraphPause => ParagraphItemType.ParagraphPause,
        PauseKind.ChapterPause => ParagraphItemType.ChapterPause,
        PauseKind.PartPause => ParagraphItemType.PartPause,
        PauseKind.VolumePause => ParagraphItemType.VolumePause,
        // Total today. Left to throw rather than defaulting to Pause, so a new PauseKind is a
        // visible defect instead of a silently wrong pause.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown pause kind."),
    };

    public async Task<BookMutationEffects> ApplyAsync(
        InsertPauseParagraphMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var anchor = await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == mutation.AnchorItemId, ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No paragraph item {mutation.AnchorItemId} to insert a pause against.");

        var anchorParagraph = await db.Paragraphs.FirstOrDefaultAsync(p => p.Id == anchor.ParagraphId, ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No paragraph {anchor.ParagraphId} to insert a pause against.");

        // A pause is a Paragraph of its own, so it is ordered among the anchor's Paragraph
        // siblings — never among the anchor's own items.
        var siblings = await db.Paragraphs
            .Where(p => p.ChapterId == anchorParagraph.ChapterId)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);

        var index = siblings.FindIndex(p => p.Id == anchorParagraph.Id);
        if (index < 0)
            throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"Paragraph {anchorParagraph.Id} is not among its own Chapter's paragraphs.");

        var (afterOrder, beforeOrder) = mutation.Position == InsertPosition.Before
            ? (index > 0 ? siblings[index - 1].Order : null, siblings[index].Order)
            : (siblings[index].Order, index < siblings.Count - 1 ? siblings[index + 1].Order : (string?)null);

        var paragraph = PauseInserter.AddPauseParagraph(
            db, anchorParagraph.ChapterId, PauseTypeOf(mutation.PauseKind), afterOrder, beforeOrder);

        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact,
            Facets = BookFacets.Structure,
            CreatedId = paragraph.Id,
            ParagraphIds = [paragraph.Id],
        };
    }
}
