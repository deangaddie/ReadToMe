using Read2Me.Core.Models;
using Read2Me.Services.Commands;

namespace Read2Me.Services
{
    public class BookCommandHandler : IBookCommandHandler
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ProjectDbSession _session;

        public BookCommandHandler(IServiceProvider serviceProvider, ProjectDbSession session)
        {
            _serviceProvider = serviceProvider;
            _session = session;
        }

        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
            var handler = _serviceProvider.GetService(handlerType);

            if (handler == null)
                throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");

            var method = handlerType.GetMethod(nameof(ICommandHandler<BookCommand>.HandleAsync));
            try
            {
                return await (Task<Guid?>)method!.Invoke(handler, [command, ct])!;
            }
            finally
            {
                // Evict the cached tracking context so the next read opens a fresh one.
                // Handlers mutate through the session's long-lived DbContext (and some use
                // ExecuteDelete/ExecuteUpdate, which bypass the change tracker entirely), so
                // without eviction a follow-up read returns stale tracked entities — e.g. a
                // deleted voice still counted in Character.Voices. Mirrors BookMutations, which owns
                // the same eviction for every producer that has migrated off this façade.
                _session.Evict(command.FolderId);
            }
        }

        // Keep for backward compat with tests that reference BookCommandHandler.ApplyMutationAsync directly.
        internal static System.Threading.Tasks.Task ApplyMutationAsync(
            Read2Me.Data.ProjectDbContext db, Read2Me.Services.Books.HierarchyMutation mutation)
            => Commands.BookMutationApplier.ApplyMutationAsync(db, mutation);
    }
}
