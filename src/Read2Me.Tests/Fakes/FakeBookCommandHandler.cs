using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeBookCommandHandler : IBookCommandHandler
    {
        public List<BookCommand> Executed { get; } = new();

        public Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            Executed.Add(command);
            return Task.FromResult<Guid?>(null);
        }
    }
}
