using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;

namespace Read2Me.Services
{
    /// <summary>
    /// The legacy façade: one command, one nullable id, every refusal it did not soften raised as an
    /// exception. It is a thin wrapper over <see cref="BookCommandDispatcher"/> for the callers that
    /// still hold <see cref="IBookCommandHandler"/>, and ticket 15 deletes it with them.
    /// </summary>
    public class BookCommandHandler(BookCommandDispatcher dispatcher) : IBookCommandHandler
    {
        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default) =>
            LegacyBookCommandBridge.Flatten(await dispatcher.ExecuteAsync(command, ct), ct);

        // Keep for backward compat with tests that reference BookCommandHandler.ApplyMutationAsync directly.
        internal static System.Threading.Tasks.Task ApplyMutationAsync(
            Read2Me.Data.ProjectDbContext db, Read2Me.Services.Books.HierarchyMutation mutation)
            => Commands.BookMutationApplier.ApplyMutationAsync(db, mutation);
    }
}
