using System.Threading.Channels;

namespace Read2Me.Services.Queueing;

public interface IQueueSource<TItem>
{
    ChannelReader<TItem> Reader { get; }
}
