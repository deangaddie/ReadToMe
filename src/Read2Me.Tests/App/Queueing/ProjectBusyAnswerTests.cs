using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Queueing;

/// <summary>
/// The per-project busy answer over the real queues. The voice batch and Assembly sources are
/// covered at their own <c>BusyFor</c>, where their harnesses can hold a run open; here they stay
/// idle.
/// </summary>
public class ProjectBusyAnswerTests
{
    private static readonly ProjectFolderId Folder = new("test-book");
    private static readonly ProjectFolderId OtherFolder = new("other-book");

    private readonly ProjectActivity _activity = NeverBusy.Activity();
    private readonly CharacterQueueService _characters;
    private readonly AudioQueueService _audio;

    public ProjectBusyAnswerTests() => (_characters, _audio) = (new(_activity), new(_activity));

    private ProjectBusyAnswer Sut() => new(
        _characters,
        _audio,
        UnscopedVoiceBatch.Runner(_activity),
        new AudiobookAssemblyService(
            null!, null!, new EventBroadcaster<AssemblyEvent>(), null!,
            NullLogger<AudiobookAssemblyService>.Instance));

    private static QueuedParagraph Paragraph(ProjectFolderId folder) =>
        new(folder, Guid.NewGuid(), "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static QueuedAudioItem AudioItem(ProjectFolderId folder) =>
        new(folder, new AudioItemRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void For_IdleProject_IsNull()
    {
        Assert.Null(Sut().For(Folder));
    }

    [Fact]
    public void For_CharacterQueueItems_CountsQueuedAndProcessing()
    {
        var items = Enumerable.Range(0, 3).Select(_ => Paragraph(Folder)).ToArray();
        _characters.Enqueue(items);
        _characters.MarkProcessing(items[0]);

        var busy = Sut().For(Folder);

        Assert.NotNull(busy);
        Assert.Equal("Attribution: 3 paragraphs left", busy.Reason);
        Assert.Equal([BusySourceKind.CharacterQueue], busy.Sources.Select(s => s.Kind));
    }

    [Fact]
    public void For_OneCharacterQueueItem_IsSingular()
    {
        _characters.Enqueue([Paragraph(Folder)]);

        Assert.Equal("Attribution: 1 paragraph left", Sut().For(Folder)?.Reason);
    }

    [Fact]
    public void For_SettledCharacterQueueItems_AreNotBusy()
    {
        var done = Paragraph(Folder);
        var failed = Paragraph(Folder);
        _characters.Enqueue([done, failed]);
        _characters.Apply(done, new Disposition.Complete(1));
        _characters.Apply(failed, new Disposition.Failed("boom"));

        Assert.Null(Sut().For(Folder));
    }

    [Fact]
    public void For_AudioQueueItems_CountsTheOneGenerating()
    {
        var items = new[] { AudioItem(Folder), AudioItem(Folder) };
        _audio.Enqueue(items);
        _audio.MarkProcessing(items[0]);

        var busy = Sut().For(Folder);

        Assert.NotNull(busy);
        Assert.Equal("Audio: 2 items left", busy.Reason);
        Assert.Equal([BusySourceKind.AudioQueue], busy.Sources.Select(s => s.Kind));
    }

    [Fact]
    public void For_AfterTheQueuesAreCancelled_IsNull()
    {
        var paragraph = Paragraph(Folder);
        var audio = AudioItem(Folder);
        _characters.Enqueue([paragraph]);
        _characters.MarkProcessing(paragraph);
        _audio.Enqueue([audio]);
        _audio.MarkProcessing(audio);

        _characters.CancelAll();
        _audio.CancelAll();

        Assert.Null(Sut().For(Folder));
    }

    [Fact]
    public void For_AnotherProjectsWork_DoesNotMakeThisProjectBusy()
    {
        _characters.Enqueue([Paragraph(OtherFolder)]);
        _audio.Enqueue([AudioItem(OtherFolder)]);

        Assert.Null(Sut().For(Folder));
        Assert.NotNull(Sut().For(OtherFolder));
    }

    [Fact]
    public void For_SeveralSourcesBusy_NamesEachInOrder()
    {
        _characters.Enqueue([Paragraph(Folder), Paragraph(Folder)]);
        _audio.Enqueue([AudioItem(Folder)]);

        var busy = Sut().For(Folder);

        Assert.NotNull(busy);
        Assert.Equal("Attribution: 2 paragraphs left · Audio: 1 item left", busy.Reason);
        Assert.Equal(
            [BusySourceKind.CharacterQueue, BusySourceKind.AudioQueue],
            busy.Sources.Select(s => s.Kind));
    }
}
