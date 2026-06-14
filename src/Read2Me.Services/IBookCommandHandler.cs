using System;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;

namespace Read2Me.Services;

public interface IBookCommandHandler
{
    /// <summary>
    /// Executes a book command. For Split* commands, returns the Id of the
    /// newly created parent entity (new Volume/Part/Chapter/Paragraph); null otherwise.
    /// </summary>
    Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default);
}
