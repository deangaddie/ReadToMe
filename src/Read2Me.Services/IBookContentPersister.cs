using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services
{
    public interface IBookContentPersister
    {
        Task PersistAsync(ProjectDbContext db, BookContent content, CancellationToken cancellationToken = default);
    }
}
