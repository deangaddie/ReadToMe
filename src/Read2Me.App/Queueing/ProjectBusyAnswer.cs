using System.Linq;
using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Characters;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Queueing;

/// <summary>
/// Answers "is anything queued or running for this project, and what?" in one place: the
/// Character Queue, the Audio Queue, the voice batch and an Assembly, each asked only about the
/// project's own folder. Null means the project is idle. Viewing a step and Restore gate on it,
/// and Background runs are tracked from its Character Queue and voice batch sources.
/// </summary>
public sealed class ProjectBusyAnswer(
    CharacterQueueService characterQueue,
    AudioQueueService audioQueue,
    VoiceBatchRunner voiceBatch,
    AudiobookAssemblyService assembly)
{
    public ProjectBusy? For(ProjectFolderId folder)
    {
        BusySource?[] sources =
        [
            characterQueue.BusyFor(folder),
            audioQueue.BusyFor(folder),
            voiceBatch.BusyFor(folder),
            assembly.BusyFor(folder),
        ];
        var busy = sources.OfType<BusySource>().ToArray();
        return busy.Length == 0 ? null : new ProjectBusy(busy);
    }
}
