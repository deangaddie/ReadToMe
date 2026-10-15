using Read2Me.Data;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// The first mutation family to cross <see cref="BookMutations"/> end to end: it stages one new
/// Speech ParagraphItem beside an anchor and reports what it created.
/// <para>
/// <c>POST /api/projects/{folder}/commands</c> resolves any command by name, so an agent can post
/// this one with no dialog in front of it. Its refusals are the Item insertion rules, shared with
/// <see cref="InsertNodeMutation"/> at a paragraph's start or end and decided in
/// <see cref="Books.BookHierarchy.PlanInsertParagraphItem"/>: blank text and a pause anchor are
/// <see cref="BookMutationRejection.Validation"/>; an anchor the Book does not contain is
/// <see cref="BookMutationRejection.NotFound"/>.
/// </para>
/// </summary>
public sealed class InsertParagraphItemMutationImplementation
    : IBookMutationImplementation<InsertParagraphItemMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        InsertParagraphItemMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var plan = hierarchy.PlanInsertParagraphItem(mutation.AnchorItemId, mutation.Position, mutation.Text);
        return BookMutationApplier.ApplyPlan(db, plan, InsertionEffects.Of);
    }
}
