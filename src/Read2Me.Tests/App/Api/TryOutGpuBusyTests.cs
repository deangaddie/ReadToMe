using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Api;
using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Api;

/// <summary>
/// Why a Pronunciation try-out can't play, over the real queues. Nothing processes them here, so
/// enqueued work stays queued: the E2E API tests prove each holder while it runs, this proves
/// queued work holds the GPU too.
/// </summary>
public class TryOutGpuBusyTests
{
    private static readonly ProjectFolderId Folder = new("other-book");

    private readonly CharacterQueueService _characters;
    private readonly AudioQueueService _audio;
    private readonly VoiceBatchRunner _voiceBatch;

    public TryOutGpuBusyTests()
    {
        var activity = NeverBusy.Activity();
        (_characters, _audio) = (new(activity), new(activity));
        _voiceBatch = new(
            null!,
            activity,
            NullLogger<VoiceBatchRunner>.Instance,
            new EventBroadcaster<VoiceBatchEvent>(),
            new EventBroadcaster<LlmStreamEvent>());
    }

    private string? Reason() => PronunciationEndpoints.GpuBusyReason(_characters, _audio, _voiceBatch);

    [Fact]
    public void Idle_queues_let_a_try_out_play()
    {
        Assert.Null(Reason());
    }

    [Fact]
    public void Attribution_queued_but_not_started_holds_the_GPU()
    {
        _characters.Enqueue([new QueuedParagraph(Folder, Guid.NewGuid(), "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())]);

        Assert.Equal("Can't play while Attribution is running: it has the GPU.", Reason());
    }

    [Fact]
    public void Audio_generation_queued_but_not_started_holds_the_GPU()
    {
        _audio.Enqueue([new QueuedAudioItem(Folder,
            new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))]);

        Assert.Equal("Can't play while Audio generation is running: it has the GPU.", Reason());
    }
}
