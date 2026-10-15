using Read2Me.Data;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Stages one new node beside or inside an anchor and reports what it created.
/// <para>
/// The commands endpoint dispatches this with no dialog in front of it, but every refusal — an
/// unknown anchor, a combination that creates no node, blank text, an item at a pause paragraph's
/// start or end — is decided by <see cref="Books.BookHierarchy.PlanInsertNode"/>, beside the split and
/// merge logic that reasons over the same sibling lists. This only loads, plans and reports.
/// </para>
/// </summary>
public sealed class InsertNodeMutationImplementation : IBookMutationImplementation<InsertNodeMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        InsertNodeMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var plan = hierarchy.PlanInsertNode(
            mutation.AnchorKind, mutation.AnchorId, mutation.Placement, mutation.Title, mutation.Text);
        return BookMutationApplier.ApplyPlan(db, plan, InsertionEffects.Of);
    }
}
