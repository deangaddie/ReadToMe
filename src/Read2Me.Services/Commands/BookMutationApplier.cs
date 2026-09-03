using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;

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
    /// Puts a planned mutation on the change tracker without saving it. Callers that own a
    /// transaction and a single commit point — <c>BookMutations</c> — stage and save themselves;
    /// legacy command handlers use <see cref="ApplyMutationAsync"/>, which saves immediately.
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

    internal static async Task ApplyMutationAsync(ProjectDbContext db, HierarchyMutation mutation)
    {
        StageMutation(db, mutation);
        await db.SaveChangesAsync();
    }

    internal static async Task<HierarchyMutation?> PlanAndApplyAsync(
        ProjectDbContext db,
        Func<BookHierarchy, HierarchyMutation?> planner)
    {
        var h = await LoadBookHierarchyAsync(db);
        var mutation = planner(h);
        if (mutation != null)
            await ApplyMutationAsync(db, mutation);
        return mutation;
    }
}
