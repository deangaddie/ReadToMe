using Read2Me.Core.Models;

namespace Read2Me.Services.Commands;

public interface ICommandHandler<in TCommand> where TCommand : BookCommand
{
    Task<Guid?> HandleAsync(TCommand command, CancellationToken ct);
}
