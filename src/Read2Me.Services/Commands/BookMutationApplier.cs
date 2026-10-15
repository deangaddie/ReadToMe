using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

internal static class BookMutationApplier
{
    internal static async Task<BookHierarchy> LoadBookHierarchyAsync(ProjectDbContext db)
    {
        var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        var parts = await db.Parts.OrderBy(p => p.Order).ToListAsync();
        var chapters = await db.Chapters.OrderBy(c => c.Order).ToListAsync();
        var paragraphs = await db.Paragraphs.OrderBy(p => p.Order).ToListAsync();
        var items = await db.ParagraphItems.OrderBy(i => i.Order).ToListAsync();
        return new BookHierarchy
        {
            Volumes = volumes,
            Parts = parts.GroupBy(p => p.VolumeId).ToDictionary(g => g.Key, g => g.ToList()),
            Chapters = chapters.GroupBy(c => c.PartId).ToDictionary(g => g.Key, g => g.ToList()),
            Paragraphs = paragraphs.GroupBy(p => p.ChapterId).ToDictionary(g => g.Key, g => g.ToList()),
            Items = items.GroupBy(i => i.ParagraphId).ToDictionary(g => g.Key, g => g.ToList()),
        };
    }

    /// <summary>
    /// Maps a Book Hierarchy plan onto a mutation's answer: Refused throws the rejection it names,
    /// NoChange is <see cref="BookMutationEffects.Nothing"/>, and Planned is staged and reported by
    /// <paramref name="effects"/>.
    /// </summary>
    internal static BookMutationEffects ApplyPlan(
        ProjectDbContext db, HierarchyPlan plan, Func<HierarchyPlan.Planned, BookMutationEffects> effects)
    {
        switch (plan)
        {
            case HierarchyPlan.Refused refused:
                throw new BookMutationRejectedException(refused.Rejection, refused.Reason);
            case HierarchyPlan.Planned planned:
                StageMutation(db, planned.Mutation);
                return effects(planned);
            default:
                return BookMutationEffects.Nothing;
        }
    }

    /// <summary>
    /// Puts a planned mutation on the change tracker without saving it. Saving is not this class's
    /// business: the mutation implementations stage here, and <c>BookMutations</c> owns the
    /// transaction and the one commit point (ADR 0007).
    /// </summary>
    internal static void StageMutation(ProjectDbContext db, HierarchyMutation mutation)
    {
        foreach (var entity in mutation.ToAdd)
        {
            switch (entity)
            {
                case Volume v: db.Volumes.Add(v); break;
                case Part p: db.Parts.Add(p); break;
                case Chapter c: db.Chapters.Add(c); break;
                case Paragraph pg: db.Paragraphs.Add(pg); break;
                case ParagraphItem i: db.ParagraphItems.Add(i); break;
                default: throw new NotSupportedException($"Unhandled book entity {entity.GetType().Name}");
            }
        }
        foreach (var entity in mutation.ToDelete)
        {
            switch (entity)
            {
                case Volume v: db.Volumes.Remove(v); break;
                case Part p: db.Parts.Remove(p); break;
                case Chapter c: db.Chapters.Remove(c); break;
                case Paragraph pg: db.Paragraphs.Remove(pg); break;
                case ParagraphItem i: db.ParagraphItems.Remove(i); break;
                default: throw new NotSupportedException($"Unhandled book entity {entity.GetType().Name}");
            }
        }
        foreach (var entity in mutation.ToUpdate)
        {
            db.Entry(entity).State = EntityState.Modified;
        }
    }
}
