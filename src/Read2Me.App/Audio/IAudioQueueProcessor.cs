using Read2Me.App.Queueing;
using Read2Me.Services.Audio;

namespace Read2Me.App.Audio;

public interface IAudioQueueProcessor : IQueueProcessor<QueuedAudioItem>
{
}
