using Read2Me.Core.Models;

namespace Read2Me.Services.Commands;

/// <summary>
/// Runs one <see cref="BookCommand"/> and answers in the terms its wire contract is written in —
/// the mutation outcome, and the identity <c>POST /api/projects/{folder}/commands</c> reports.
/// <para>
/// This is the command families' shared entry, sitting between the endpoint adapter and the
/// handlers that translate a command into a <see cref="Mutations.BookMutation"/>. It is the seam the
/// endpoint depends on: the legacy <see cref="IBookCommandHandler"/> façade is now only a lossy
/// wrapper over it, for the two callers that still take a <c>Guid?</c>.
/// </para>
/// </summary>
public sealed class BookCommandDispatcher(IServiceProvider serviceProvider, ProjectDbSession session)
{
    public async Task<BookCommandResult> ExecuteAsync(BookCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        var handler = serviceProvider.GetService(handlerType)
            ?? throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");

        var method = handlerType.GetMethod(nameof(ICommandHandler<BookCommand>.HandleAsync));
        try
        {
            return await (Task<BookCommandResult>)method!.Invoke(handler, [command, ct])!;
        }
        finally
        {
            // Evict the cached tracking context so the next read opens a fresh one. Every handler
            // now writes through BookMutations, which evicts for itself — but CreateCharacter
            // resolves through a read, and a handler that refuses before committing never reaches
            // that eviction, so the belt stays until the façade goes (ticket 15).
            session.Evict(command.FolderId);
        }
    }
}
