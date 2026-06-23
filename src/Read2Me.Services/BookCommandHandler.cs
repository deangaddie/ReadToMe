using Read2Me.Core.Models;
using Read2Me.Services.Commands;

namespace Read2Me.Services
{
    public class BookCommandHandler : IBookCommandHandler
    {
        private readonly IServiceProvider _serviceProvider;

        public BookCommandHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
            var handler = _serviceProvider.GetService(handlerType);

            if (handler != null)
            {
                var method = handlerType.GetMethod(nameof(ICommandHandler<BookCommand>.HandleAsync));
                return await (Task<Guid?>)method!.Invoke(handler, [command, ct])!;
            }

            throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");
        }

        // Keep for backward compat with tests that reference BookCommandHandler.ApplyMutationAsync directly.
        internal static System.Threading.Tasks.Task ApplyMutationAsync(
            Read2Me.Data.ProjectDbContext db, Read2Me.Services.Books.HierarchyMutation mutation)
            => Commands.BookMutationApplier.ApplyMutationAsync(db, mutation);
    }
}
