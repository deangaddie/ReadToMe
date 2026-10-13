using System.Diagnostics;
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
            "audiocpp:waitingToStop", "llama:waitingToStart",
            "audiocpp:stopping", "audiocpp:stopped",
            "llama:starting", "llama:ready",
        ], messages.Where(m => m.Kind == "stage").Select(m => $"{m.Name}:{m.Stage}"));
        Assert.Equal(["shutdown:audiocpp", "start:llama"], _control.OpLog);
        var done = messages[^1];
        Assert.Equal("done", done.Kind);
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
        var failed = Assert.Single(messages, m => m.Stage == "failed");
        Assert.Equal("llama", failed.Name);
        Assert.Equal("health check timed out", failed.Error);
        var done = messages[^1];
        Assert.Equal("done", done.Kind);
        Assert.False(done.Ok);
        Assert.Contains("health check timed out", done.Reason);
    }

    /// <summary>Waits for the run's <c>done</c>, then returns every preflight message in send order.</summary>
    private async Task<List<PreflightMessage>> RunMessagesAsync()
    {
        var sw = Stopwatch.StartNew();
        while (!_hub.Method("preflight").Any(m => ((PreflightMessage)m.Payload).Kind == "done"))
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting for the run's done");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return [.. _hub.Method("preflight").Select(m => (PreflightMessage)m.Payload)];
    }
}
