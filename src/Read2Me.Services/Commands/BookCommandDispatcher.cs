using Read2Me.Core.Models;

namespace Read2Me.Services.Commands;

/// <summary>
/// Runs one <see cref="BookCommand"/> by its row in <see cref="BookCommandTable"/>, and answers in
/// the terms its wire contract is written in: the mutation outcome, and the identity
/// <c>POST /api/projects/{folder}/commands</c> reports.
/// <para>
/// Nothing else in the app reaches it: the wire contract is the only reason a Book mutation is ever
/// named by a command rather than committed directly (ADR 0007).
/// </para>
/// </summary>
public sealed class BookCommandDispatcher(IServiceProvider serviceProvider)
{
    // No eviction of its own. Every row's write goes through BookMutations, which evicts the
    // tracking session for itself once its transaction is done; a handler that refuses before
    // committing wrote nothing for a later read to be stale about.
    public Task<BookCommandResult> ExecuteAsync(BookCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return BookCommandTable.Of(command).ExecuteAsync(command, serviceProvider, ct);
    }
}
