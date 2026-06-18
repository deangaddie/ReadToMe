using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Characters;

namespace Read2Me.App.Characters
{
    public interface ICharacterQueueProcessor
    {
        Task ProcessItemAsync(QueuedParagraph item, CancellationToken hostCt);
    }
}
