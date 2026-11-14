using System.Threading.Channels;

namespace Read2Me.Services.Queueing;

public interface IQueueSource<TItem>
{
    ChannelReader<TItem> Reader { get; }

    /// <summary>
    /// The <b>Worker turn</b> (CONTEXT: queueing) for an item just read off <see cref="Reader"/>: the
    /// worker disposes it once the item's work has let go of its project, and until then that
    /// project is busy. Null when Cancel all dropped the item before its turn began; the worker
    /// skips it.
    /// </summary>
    IDisposable? BeginTurn(TItem item);
}
