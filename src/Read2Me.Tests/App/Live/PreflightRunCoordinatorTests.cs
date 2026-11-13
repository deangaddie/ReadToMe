using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Live;
using Read2Me.App.Services.Preflight;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// A pre-flight run as the caller's connection sees it: every service's stage on the wire, in order,
/// then <c>done</c>. The state machine itself is <see cref="Read2Me.Tests.App.Preflight.AiPreflightProgressTests"/>;
/// this proves the coordinator names the stages and carries a failure through to the client.
/// </summary>
public class PreflightRunCoordinatorTests
{
    private static readonly DockerAiService Llama = new("llama", "read2me-llama", "http://localhost:8080", "/health", UsesGpu: true);
    private static readonly DockerAiService Audiocpp = new("audiocpp", "read2me-audiocpp", "http://localhost:8004", "/health", UsesGpu: true);

    private readonly FakeAiServiceControl _control = new();
    private readonly RecordingHubContext _hub = new();

    private PreflightRunCoordinator Coordinator() => new(
        new ObservedAiServiceControl(_control, new EventBroadcaster<ServiceStatusChanged>()),
        _hub, NullLogger<PreflightRunCoordinator>.Instance);

    private static AiPreflightPlan Plan(DockerAiService[] toStart, DockerAiService[] conflicts) =>
        new([.. toStart.Select(s => new AiPreflightItem(s, AiServiceStatus.Stopped))], [.. conflicts]);

    [Fact]
    public async Task A_gpu_conflict_is_stopped_before_the_required_service_starts_and_every_stage_reaches_the_caller()
    {
        var run = Coordinator().Start(Plan([Llama], [Audiocpp]), "conn-1");

        var messages = await RunMessagesAsync();

        Assert.All(messages, m => Assert.Equal(run, m.Run));
        Assert.Equal(
        [
            "audiocpp:WaitingToStop", "llama:WaitingToStart",
            "audiocpp:Stopping", "audiocpp:Stopped",
            "llama:Starting", "llama:Ready",
        ], messages.Where(m => m.Kind == PreflightMessageKind.Stage).Select(m => $"{m.Name}:{m.Stage}"));
        Assert.Equal(["shutdown:audiocpp", "start:llama"], _control.OpLog);
        var done = messages[^1];
        Assert.Equal(PreflightMessageKind.Done, done.Kind);
        Assert.True(done.Ok);
        Assert.Null(done.Reason);
        Assert.All(_hub.Messages, m => Assert.Equal("client:conn-1", m.Target));
    }

    [Fact]
    public async Task A_failed_start_reports_its_error_on_the_stage_and_ends_the_run_with_the_reason()
    {
        _control.StartResultByName["llama"] = new AiServiceOpResult(false, AiServiceStatus.Down, "health check timed out");

        Coordinator().Start(Plan([Llama], []), "conn-1");

        var messages = await RunMessagesAsync();
        var failed = Assert.Single(messages, m => m.Stage == PreflightStage.Failed);
        Assert.Equal("llama", failed.Name);
        Assert.Equal("health check timed out", failed.Error);
        var done = messages[^1];
        Assert.Equal(PreflightMessageKind.Done, done.Kind);
        Assert.False(done.Ok);
        Assert.Contains("health check timed out", done.Reason);
    }

    [Fact]
    public async Task A_caller_that_disconnects_mid_run_still_lets_every_op_happen()
    {
        _hub.Disconnect("conn-1");
        var logger = new CollectingLogger<PreflightRunCoordinator>();
        var coordinator = new PreflightRunCoordinator(
            new ObservedAiServiceControl(_control, new EventBroadcaster<ServiceStatusChanged>()), _hub, logger);

        var run = coordinator.Start(Plan([Llama], [Audiocpp]), "conn-1");

        // The done send is the run's last act; the debug line says its failure was swallowed.
        await Eventually.TrueAsync(
            () => logger.At(LogLevel.Debug).ToList().Contains($"preflight Done for run {run} not delivered"),
            "the run's done to fail");
        Assert.Equal(["shutdown:audiocpp", "start:llama"], _control.OpLog);
        Assert.Empty(_hub.Messages);
    }

    /// <summary>Waits for the run's <c>done</c>, then returns every preflight message in send order.</summary>
    private async Task<List<PreflightMessage>> RunMessagesAsync()
    {
        await Eventually.TrueAsync(
            () => _hub.Method("preflight").Any(m => ((PreflightMessage)m.Payload).Kind == PreflightMessageKind.Done),
            "the run's done");
        return [.. _hub.Method("preflight").Select(m => (PreflightMessage)m.Payload)];
    }
}
