using Read2Me.Core.Models;

namespace Read2Me.Services.Commands;

/// <summary>
/// Runs one <see cref="BookCommand"/> and answers in the terms its wire contract is written in —
/// the mutation outcome, and the identity <c>POST /api/projects/{folder}/commands</c> reports.
/// <para>
/// This is the command families' shared entry, sitting between the endpoint adapter and the
/// handlers that translate a command into a <see cref="Mutations.BookMutation"/>. Nothing else in
/// the app reaches it: the wire contract is the only reason a Book mutation is ever named by a
/// command rather than committed directly (ADR 0007).
/// </para>
/// </summary>
public sealed class BookCommandDispatcher(IServiceProvider serviceProvider)
{
    public async Task<BookCommandResult> ExecuteAsync(BookCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        var handler = serviceProvider.GetService(handlerType)
            ?? throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");

        var method = handlerType.GetMethod(nameof(ICommandHandler<BookCommand>.HandleAsync));

        // No eviction of its own. Every handler's write goes through BookMutations, which evicts
        // the tracking session for itself once its transaction is done; a handler that refuses
        // before committing wrote nothing for a later read to be stale about.
        return await (Task<BookCommandResult>)method!.Invoke(handler, [command, ct])!;
    }
}
