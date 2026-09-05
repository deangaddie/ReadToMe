using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the manual and AI edit family shares (ADR 0007). Every mutation here rewrites content a
/// reader is looking at — a node's title, an item's text — and none of them creates, deletes or
/// moves a node, so each one names exactly what it rewrote.
/// <para>
/// The two halves are one family because they write the same two things, and both rules live here
/// once. An AI edit program reached item text by its own path before this, which is how the
/// stale-audio rule came to be closed on the item menu and left open on the AI route; the halves
/// disagree now about one thing only, which is what an absent target means — a hand edit refuses it,
/// a program skips it.
/// </para>
/// </summary>
internal static class BookEditEffects
{
    /// <summary>What one retitle did, so both halves can act on the same three answers.</summary>
    public enum Retitle { NotFound, Unchanged, Applied }

    /// <summary>
    /// Rewrites one node's title, whichever level it names. A title is not on a Paragraph, so the
    /// facet a reader gets back is one no targeted refresh can place on a row.
    /// </summary>
    public static async Task<Retitle> RetitleAsync(
        ProjectDbContext db, BookEditTargetKind kind, Guid id, string title, CancellationToken ct)
    {
        return kind switch
        {
            BookEditTargetKind.VolumeTitle => Apply(
                await db.Volumes.FirstOrDefaultAsync(v => v.Id == id, ct),
                v => v.Title, (v, t) => v.Title = t),
            BookEditTargetKind.PartTitle => Apply(
                await db.Parts.FirstOrDefaultAsync(p => p.Id == id, ct),
                p => p.Title, (p, t) => p.Title = t),
            BookEditTargetKind.ChapterTitle => Apply(
                await db.Chapters.FirstOrDefaultAsync(c => c.Id == id, ct),
                c => c.Title, (c, t) => c.Title = t),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a titled node."),
        };

        Retitle Apply<TNode>(TNode? node, Func<TNode, string?> read, Action<TNode, string> write)
            where TNode : class
        {
            if (node is null) return Retitle.NotFound;
            if (read(node) == title) return Retitle.Unchanged;
            write(node, title);
            return Retitle.Applied;
        }
    }

    /// <summary>
    /// One node retitled by hand, as a mutation's whole effects. A node the Book does not contain is
    /// a refusal rather than a silent nothing, because a hand edit named something the producer was
    /// looking at a moment ago.
    /// </summary>
    public static async Task<BookMutationEffects> RetitleOneAsync(
        ProjectDbContext db, BookEditTargetKind kind, Guid id, string title, CancellationToken ct) =>
        await RetitleAsync(db, kind, id, title, ct) switch
        {
            Retitle.NotFound => throw NotFound(NounFor(kind), id),
            Retitle.Unchanged => BookMutationEffects.Nothing,
            _ => new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.NodeTitle,
                NodeIds = [id],
            },
        };

    private static string NounFor(BookEditTargetKind kind) => kind switch
    {
        BookEditTargetKind.VolumeTitle => "volume",
        BookEditTargetKind.PartTitle => "part",
        BookEditTargetKind.ChapterTitle => "chapter",
        _ => "paragraph item",
    };

    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} to edit.");

    /// <summary>
    /// Applies <paramref name="newText"/> to <paramref name="item"/> and reports what that cost the
    /// item's audio.
    /// <para>
    /// A rewritten item's WAV speaks words the item no longer has, and because the item still
    /// <em>has</em> audio it is not a Generatable item: a "select needs audio" pass skips it and the
    /// mismatch assembles into the exported m4b. Clearing <c>AudioFileName</c> hands it back to the
    /// Audio Queue. The precedent stands in the opposite corner — a hand-flipped speaker discards the
    /// WAV, the queue's own attribution does not (ADR-0006) — and a hand or approved edit is the same
    /// kind of explicit statement that the current audio is wrong.
    /// </para>
    /// <para>
    /// Any <c>AudioReview</c> row is <b>deleted</b>, not dismissed. It judged audio that no longer
    /// exists, so it is stale rather than resolved, and <c>Dismissed</c> means "a human decided this
    /// was fine" — which would wrongly suppress the review of whatever is generated next. The item
    /// returns to a clean pre-generation state: no audio, no verdict, ready to be judged afresh.
    /// </para>
    /// <para>
    /// Unchanged text writes nothing, so a save that edits nothing keeps good audio and its verdict.
    /// The accepted cost, recorded rather than hidden: editing an item purely to fix a typo or a
    /// pronunciation spelling loses good audio and pays for a regeneration.
    /// </para>
    /// </summary>
    /// <returns>The facets the rewrite moved, or <see cref="BookFacets.None"/> when the text stood.</returns>
    public static async Task<BookFacets> RewriteAsync(
        ProjectDbContext db, ParagraphItem item, string? newText, CancellationToken ct)
    {
        if (item.Text == newText) return BookFacets.None;

        // Reported only where there was something to lose: the facets say what this write actually
        // did, and an item with no audio gives a reader no audio state to reread.
        var facets = BookFacets.ItemText;
        if (item.AudioFileName is not null) facets |= BookFacets.Audio;

        item.Text = newText;
        item.AudioFileName = null;

        var reviews = await db.AudioReviews
            .Where(r => r.ParagraphItemId == item.Id)
            .ToListAsync(ct);
        if (reviews.Count > 0)
        {
            db.AudioReviews.RemoveRange(reviews);
            facets |= BookFacets.Reviews;
        }

        return facets;
    }
}

/// <summary>
/// Rewrites one node's title. Every level is the same gesture, so each of these only says which
/// level it names; the rule itself is <see cref="BookEditEffects.RetitleOneAsync"/>.
/// </summary>
public sealed class UpdateVolumeTitleMutationImplementation
    : IBookMutationImplementation<UpdateVolumeTitleMutation>
{
    public Task<BookMutationEffects> ApplyAsync(
        UpdateVolumeTitleMutation mutation, ProjectDbContext db, CancellationToken ct) =>
        BookEditEffects.RetitleOneAsync(
            db, BookEditTargetKind.VolumeTitle, mutation.VolumeId, mutation.Title, ct);
}

public sealed class UpdatePartTitleMutationImplementation
    : IBookMutationImplementation<UpdatePartTitleMutation>
{
    public Task<BookMutationEffects> ApplyAsync(
        UpdatePartTitleMutation mutation, ProjectDbContext db, CancellationToken ct) =>
        BookEditEffects.RetitleOneAsync(
            db, BookEditTargetKind.PartTitle, mutation.PartId, mutation.Title, ct);
}

public sealed class UpdateChapterTitleMutationImplementation
    : IBookMutationImplementation<UpdateChapterTitleMutation>
{
    public Task<BookMutationEffects> ApplyAsync(
        UpdateChapterTitleMutation mutation, ProjectDbContext db, CancellationToken ct) =>
        BookEditEffects.RetitleOneAsync(
            db, BookEditTargetKind.ChapterTitle, mutation.ChapterId, mutation.Title, ct);
}

/// <summary>
/// Rewrites one item's text. This is also what stands behind
/// <c>POST /api/projects/{folder}/commands</c> with <c>UpdateParagraphItemText</c>, so an agent
/// posting the command gets the same audio and review clearing without the dialog in front of it.
/// </summary>
public sealed class UpdateParagraphItemTextMutationImplementation
    : IBookMutationImplementation<UpdateParagraphItemTextMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        UpdateParagraphItemTextMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == mutation.ItemId, ct)
            ?? throw BookEditEffects.NotFound("paragraph item", mutation.ItemId);

        var facets = await BookEditEffects.RewriteAsync(db, item, mutation.Text, ct);
        if (facets == BookFacets.None) return BookMutationEffects.Nothing;

        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact,
            Facets = facets,
            ParagraphIds = [item.ParagraphId],
            ParagraphItemIds = [item.Id],
        };
    }
}

/// <summary>
/// Applies an approved AI edit program in one transaction, however many targets and facets it
/// touches. One commit rather than one per row is what stops a reader watching their Book rewritten
/// a row at a time, and it is what lets a program that only rewrote item text be reconciled by
/// rereading those Paragraphs instead of the whole Book.
/// <para>
/// A target the Book no longer contains is skipped rather than refused. The program was planned
/// against a Book the producer has been reviewing — possibly for minutes, possibly while another
/// circuit edited it — so the rows that still resolve are the ones they approved, and one vanished
/// chapter is not a reason to throw the rest away.
/// </para>
/// </summary>
public sealed class ApplyBookEditsMutationImplementation
    : IBookMutationImplementation<ApplyBookEditsMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        ApplyBookEditsMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var facets = BookFacets.None;
        var nodeIds = new List<Guid>();
        var paragraphIds = new List<Guid>();
        var itemIds = new List<Guid>();

        foreach (var edit in mutation.Edits)
        {
            ct.ThrowIfCancellationRequested();

            if (edit.Kind == BookEditTargetKind.ParagraphItemText)
            {
                var item = await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == edit.Id, ct);
                if (item is null) continue;

                var rewritten = await BookEditEffects.RewriteAsync(db, item, edit.NewValue, ct);
                if (rewritten == BookFacets.None) continue;

                facets |= rewritten;
                Name(paragraphIds, item.ParagraphId);
                Name(itemIds, item.Id);
                continue;
            }

            if (await BookEditEffects.RetitleAsync(db, edit.Kind, edit.Id, edit.NewValue, ct)
                != BookEditEffects.Retitle.Applied) continue;

            facets |= BookFacets.NodeTitle;
            Name(nodeIds, edit.Id);
        }

        if (facets == BookFacets.None) return BookMutationEffects.Nothing;

        // Exact whatever the program covered: every target it moved is named above, so a program
        // that only rewrote item text reconciles by rereading those Paragraphs, while one that moved
        // a title carries a facet no reader can place on a row and rebuilds.
        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact,
            Facets = facets,
            NodeIds = nodeIds,
            ParagraphIds = paragraphIds,
            ParagraphItemIds = itemIds,
        };
    }

    /// <summary>
    /// Records an identity the receipt will carry, once. A program may hold two rows against one
    /// target — a second pass over an item, an item and its Paragraph reached twice — and naming it
    /// twice would tell a reader to reread it twice.
    /// </summary>
    private static void Name(List<Guid> named, Guid id)
    {
        if (!named.Contains(id)) named.Add(id);
    }
}
