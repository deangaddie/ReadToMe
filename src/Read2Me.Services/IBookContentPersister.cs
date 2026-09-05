using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services
{
    /// <summary>
    /// Turns read Book content into rows on <paramref name="db"/>'s change tracker.
    /// <para>
    /// It stages only: no <c>SaveChanges</c>, no transaction, no commit. The import mutation that
    /// calls it is already inside <see cref="Mutations.BookMutations"/>' transaction, and a Book that
    /// saved itself half-way through would be the intermediate state the whole replacement exists to
    /// prevent (ADR 0007).
    /// </para>
    /// </summary>
    public interface IBookContentPersister
    {
        /// <summary>Stages the content and returns how many rows it added.</summary>
        Task<int> PersistAsync(ProjectDbContext db, BookContent content, CancellationToken cancellationToken = default);
    }
}
