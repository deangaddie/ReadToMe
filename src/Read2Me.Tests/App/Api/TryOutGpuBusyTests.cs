using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Api;
using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Api;

/// <summary>
/// Why a Pronunciation try-out can't play, over the real queues and voice batch runner. Nothing
/// processes the queues here, so enqueued work stays queued: this proves queued work holds the GPU
/// too, and a running voice batch holds it until it ends. The E2E API tests prove one holder running.
/// </summary>
public sealed class TryOutGpuBusyTests : IDisposable
{
    private static readonly ProjectFolderId Folder = new("other-book");

    private readonly CharacterQueueService _characters;
    private readonly AudioQueueService _audio;
    private readonly HeldScopes _batchScopes = new();
    private readonly VoiceBatchRunner _voiceBatch;

    public TryOutGpuBusyTests()
    {
        var activity = new ProjectActivity();
        (_characters, _audio) = (new(activity), new(activity));
        _voiceBatch = new VoiceBatchRunner(_batchScopes, activity, NullLogger<VoiceBatchRunner>.Instance,
            new EventBroadcaster<VoiceBatchEvent>(), new EventBroadcaster<LlmStreamEvent>());
    }

    public void Dispose() => _batchScopes.Release();

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

    [Fact]
    public void A_running_voice_batch_holds_the_GPU()
    {
        Assert.IsType<VoiceBatchStart.Started>(_voiceBatch.StartGeneratePrompts(Folder));

        Assert.Equal("Can't play while a voice batch is running: it has the GPU.", Reason());
    }

    /// <summary>
    /// The batch's scope factory: the batch waits in it, so it stays running until the test ends,
    /// and is then cancelled before it reads anything. The wait is bounded: were the batch ever to
    /// run its work on the caller's thread, the test would fail here rather than hang.
    /// </summary>
    private sealed class HeldScopes : IServiceScopeFactory
    {
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
        private readonly ManualResetEventSlim _released = new();

        public void Release() => _released.Set();

        public IServiceScope CreateScope()
        {
            if (!_released.Wait(HangGuard))
                throw new TimeoutException(
                    $"The voice batch held its scope for {HangGuard.TotalSeconds:0} s without the test ending: StartGeneratePrompts may be running its work synchronously.");
            throw new OperationCanceledException("The test has ended.");
        }
    }
}
