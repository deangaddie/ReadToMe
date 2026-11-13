using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.App.Live;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// The LLM test send's replies to its caller where E2E does not reach (<c>LlmSettingsApiTests</c>
/// covers done and cancel): a failed or thrown run, the run state cleared before <c>RunEnded</c>,
/// and a caller that went away mid-run.
/// </summary>
public class LlmTestRunCoordinatorTests
{
    private static readonly LlmServerConfig Config = new() { Id = 7, Name = "qwen", BaseUrl = "http://localhost:8080" };

    private readonly RecordingHubContext _hub = new();
    private readonly FakeLlmCompletionRunner _runner = new();
    private readonly EventBroadcaster<LlmStreamEvent> _llmEvents = new();
    private readonly CollectingLogger<LlmTestRunCoordinator> _logger = new();
    private int _runsEnded;

    public LlmTestRunCoordinatorTests() =>
        _llmEvents.Event += e =>
        {
            if (e is RunEnded)
                Interlocked.Increment(ref _runsEnded);
        };

    private LlmTestRunCoordinator Coordinator()
    {
        var services = new ServiceCollection();
        services.AddScoped<ILlmCompletionRunner>(_ => _runner);
        return new LlmTestRunCoordinator(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _hub, _llmEvents, _logger);
    }

    [Fact]
    public async Task A_run_that_does_not_complete_sends_failed_with_its_error()
    {
        _runner.Fails(LlmRunOutcome.ServiceUnavailable, "connection refused");

        Assert.True(Coordinator().TryStart(Config, "hi", "conn-1"));

        var reply = await ReplyAsync();
        Assert.Equal(LlmTestMessageKind.Failed, reply.Kind);
        Assert.Equal(Config.Id, reply.ConfigId);
        Assert.Equal("connection refused", reply.Reason);
    }

    [Fact]
    public async Task A_runner_that_throws_sends_failed_with_the_exception_message()
    {
        _runner.Throws(new InvalidOperationException("scope disposed"));

        Assert.True(Coordinator().TryStart(Config, "hi", "conn-1"));

        var reply = await ReplyAsync();
        Assert.Equal(LlmTestMessageKind.Failed, reply.Kind);
        Assert.Equal(Config.Id, reply.ConfigId);
        Assert.Equal("scope disposed", reply.Reason);
    }

    [Fact]
    public async Task The_running_config_is_cleared_before_RunEnded_is_published()
    {
        var coordinator = Coordinator();
        int? runningAtRunEnded = -1;
        _llmEvents.Event += e =>
        {
            if (e is RunEnded)
                runningAtRunEnded = coordinator.RunningConfigId;
        };
        _runner.Completes("hello");

        Assert.True(coordinator.TryStart(Config, "hi", "conn-1"));

        await Eventually.TrueAsync(() => Volatile.Read(ref _runsEnded) == 1, "RunEnded");
        Assert.Null(runningAtRunEnded);
    }

    [Fact]
    public async Task A_caller_that_disconnects_mid_run_still_frees_the_slot_for_the_next_test()
    {
        _hub.Disconnect("conn-1");
        _runner.Completes("hello");
        var coordinator = Coordinator();

        Assert.True(coordinator.TryStart(Config, "hi", "conn-1"));
        // The send that failed is the run's last act; the debug line says it was swallowed.
        await Eventually.TrueAsync(
            () => _logger.At(LogLevel.Debug).ToList().Contains("llmTest Done for config 7 not delivered"),
            "the first run's reply to fail");

        Assert.True(coordinator.TryStart(Config, "again", "conn-2"));
        var reply = await ReplyAsync();
        Assert.Equal(LlmTestMessageKind.Done, reply.Kind);
        Assert.All(_hub.Messages, m => Assert.Equal("client:conn-2", m.Target));
    }

    private async Task<LlmTestMessage> ReplyAsync()
    {
        await Eventually.TrueAsync(() => _hub.Method("llmTest").Count > 0, "the llmTest reply");
        return Assert.IsType<LlmTestMessage>(Assert.Single(_hub.Method("llmTest")).Payload);
    }
}
