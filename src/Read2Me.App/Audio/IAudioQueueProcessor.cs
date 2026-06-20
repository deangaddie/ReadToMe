using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Audio;

namespace Read2Me.App.Audio
{
    public interface IAudioQueueProcessor
    {
        Task ProcessItemAsync(QueuedAudioItem item, CancellationToken ct);
    }
}
