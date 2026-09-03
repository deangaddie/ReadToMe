using Read2Me.Data;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Applies one mutation family inside a transaction that <see cref="BookMutations"/> owns.
/// <para>
/// An implementation stages its writes on <paramref name="db"/> and returns the effects it
/// actually applied. It does not call <c>SaveChangesAsync</c>, does not commit, does not evict the
/// tracking session, and does not publish anything — those belong to <see cref="BookMutations"/>,
/// which is the point of the seam. Expected failures are reported by throwing
/// <see cref="BookMutationRejectedException"/>; anything else thrown is a defect.
/// </para>
/// </summary>
public interface IBookMutationImplementation<in TMutation> where TMutation : BookMutation
{
    Task<BookMutationEffects> ApplyAsync(TMutation mutation, ProjectDbContext db, CancellationToken ct);
}
