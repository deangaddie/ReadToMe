using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Applies an approved AI edit program, migrated to <see cref="BookMutations"/> (ADR 0007). A
/// program that resolved no target, or that proposed only the wording already there, answers null
/// the way it always has.
/// </summary>
public sealed class ApplyBookEditsHandler(BookMutations mutations) : ICommandHandler<ApplyBookEditsCommand>
{
    public Task<BookCommandResult> HandleAsync(ApplyBookEditsCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new ApplyBookEditsMutation(c.FolderId, c.Edits), ct);
}
