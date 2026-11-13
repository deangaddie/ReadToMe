using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Live;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Read2Me.Tests.Fakes;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// The book-edit run's replies to its caller where E2E does not reach (<c>BookEditApiTests</c> covers
/// done and cancel): a proposal that throws, and a caller that went away mid-run.
/// </summary>
public class BookEditRunCoordinatorTests
{
    private static readonly ProjectFolderId Folder = new("book-a");

    private readonly RecordingHubContext _hub = new();
    private readonly StubProposalService _proposals = new();
    private readonly CollectingLogger<BookEditRunCoordinator> _logger = new();

    private BookEditRunCoordinator Coordinator()
    {
        var services = new ServiceCollection();
        services.AddScoped<BookEditProposalService>(_ => _proposals);
        return new BookEditRunCoordinator(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _hub, _logger);
    }

    private static BookEditSession Session() =>
        new BookEditSessionStore(new ManualTimeProvider()).Create(
            Folder,
            new EditProgram(true, null, EditTargetSelector.ChapterTitle, NodeFilter.All, ParagraphFilter.All,
                new EditTransform(TransformKind.Llm, Instruction: "capitalise")),
            [Target(1), Target(2)],
            "Capitalise every chapter title");

    private static EditTarget Target(int n) =>
        new(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), $"chapter {n}", $"Chapter {n}", n, null, null);

    private static ProposedEdit Row(EditTarget t) =>
        new(t.Kind, t.Id, t.DisplayPath, t.CurrentValue, t.CurrentValue.ToUpperInvariant(), ProposalStatus.Proposed, null);

    [Fact]
    public async Task A_proposal_that_throws_fails_the_session_and_tells_the_caller_why()
    {
        _proposals.Propose = (_, _) => throw new InvalidOperationException("llama went away");
        var session = Session();

        Assert.True(Coordinator().TryStart(session, thinking: false, "conn-1"));

        await Eventually.TrueAsync(() => !_hub.Messages.IsEmpty, "the bookEdit reply");
        var sent = Assert.Single(_hub.Messages);
        Assert.Equal("client:conn-1", sent.Target);
        var message = Assert.IsType<BookEditMessage>(sent.Payload);
        Assert.Equal(BookEditMessageKind.Failed, message.Kind);
        Assert.Equal(session.Id, message.Program);
        Assert.Equal("llama went away", message.Reason);
        var snapshot = session.Snapshot();
        Assert.Equal(BookEditRunStatus.Failed, snapshot.Status);
        Assert.Equal("llama went away", snapshot.Reason);
    }

    [Fact]
    public async Task A_caller_that_disconnects_mid_run_does_not_stop_the_rows_landing_on_the_session()
    {
        _hub.Disconnect("conn-1");
        _proposals.Propose = (targets, progress) =>
        {
            progress?.Report((1, targets.Count));
            progress?.Report((2, targets.Count));
            return [.. targets.Select(Row)];
        };
        var session = Session();

        Assert.True(Coordinator().TryStart(session, thinking: false, "conn-1"));

        // The done send is the run's last act; the debug line says its failure was swallowed.
        await Eventually.TrueAsync(
            () => _logger.At(LogLevel.Debug).ToList().Contains($"bookEdit Done for {session.Id} not delivered"),
            "the run's done to fail");
        var snapshot = session.Snapshot();
        Assert.Equal(BookEditRunStatus.Completed, snapshot.Status);
        Assert.Equal(session.Targets.Select(t => t.Id), snapshot.Rows.Select(r => r.Id));
        Assert.Empty(_hub.Messages);
    }

    /// <summary>The proposal service with its work replaced; the base's dependencies are never touched.</summary>
    private sealed class StubProposalService()
        : BookEditProposalService(null!, null!, null!, null!, NullLogger<BookEditProposalService>.Instance)
    {
        public Func<IReadOnlyList<EditTarget>, IProgress<(int Done, int Total)>?, IReadOnlyList<ProposedEdit>> Propose { get; set; } =
            (_, _) => [];

        public override Task<IReadOnlyList<ProposedEdit>> ProposeAsync(
            ProjectFolderId folderId, EditProgram program, IReadOnlyList<EditTarget> targets,
            IProgress<(int Done, int Total)>? progress, bool disableThinking, CancellationToken ct) =>
            Task.FromResult(Propose(targets, progress));
    }
}
