using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.App.Queueing;

public interface IQueueProcessor<TItem>
{
    Task ProcessItemAsync(TItem item, CancellationToken ct);
}
