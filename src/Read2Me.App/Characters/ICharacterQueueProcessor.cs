using Read2Me.App.Queueing;
using Read2Me.Services.Characters;

namespace Read2Me.App.Characters;

public interface ICharacterQueueProcessor : IQueueProcessor<QueuedParagraph>
{
}
