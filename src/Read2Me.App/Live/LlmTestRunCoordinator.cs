using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.App.Live;

/// <summary>
/// Runs the LLM settings page's test send in the background — the request that started it has
/// answered 202 — exactly as Blazor's page does: a Throughput Run of one, free-text shape, tokens
/// on the LLM event bus (so <c>stream:llm</c>). Only how it ended goes to the caller's hub
/// connection. One test at a time, process-wide. Each run opens its own DI scope because the
/// completion runner is scoped and the request scope is gone.
/// </summary>
public sealed class LlmTestRunCoordinator(
    IServiceScopeFactory scopes,
    IHubContext<LiveHub, ILiveClient> hub,
    EventBroadcaster<LlmStreamEvent> llmEvents,
    ILogger<LlmTestRunCoordinator> logger)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int? _configId;

    /// <summary>The config under test, or null when nothing is running.</summary>
    public int? RunningConfigId
    {
        get { lock (_gate) return _configId; }
    }

    /// <summary>False when a test is already in flight.</summary>
    public bool TryStart(LlmServerConfig config, string prompt, string? connectionId)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null)
                return false;
            cts = _cts = new CancellationTokenSource();
            _configId = config.Id;
        }

        _ = Task.Run(() => RunAsync(config, prompt, connectionId, cts), CancellationToken.None);
        return true;
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    private async Task RunAsync(LlmServerConfig config, string prompt, string? connectionId, CancellationTokenSource cts)
    {
        LlmTestMessage outcome;
        // A test send is a Throughput Run of one — deliberately, so the page shows the same
        // headline as every other surface rather than a figure of its own.
        llmEvents.Publish(new RunStarted());
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<ILlmCompletionRunner>();
            var result = await runner.RunAsync(
                new LlmRunRequest(config, prompt, $"Test send — {config.Name}", Shape: CompletionShape.None),
                cts.Token);
            outcome = result.Outcome == LlmRunOutcome.Completed
                ? LiveMessageMapper.LlmTestDone(config.Id)
                : LiveMessageMapper.LlmTestFailed(config.Id, result.Error ?? result.Outcome.ToString());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // User stopped the stream — whatever was received stays on the stream view.
            outcome = LiveMessageMapper.LlmTestCancelled(config.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM test send against {Config} failed", config.Name);
            outcome = LiveMessageMapper.LlmTestFailed(config.Id, ex.Message);
        }
        finally
        {
            // Cleared before RunEnded goes out: a client that reads the run state back on seeing
            // runEnded must not find this run still listed.
            lock (_gate)
            {
                _cts = null;
                _configId = null;
            }
            cts.Dispose();
            llmEvents.Publish(new RunEnded());
        }

        if (connectionId is null)
            return;
        try
        {
            await hub.Clients.Client(connectionId).LlmTest(outcome);
        }
        catch (Exception ex)
        {
            // A closed connection is not the run's problem: the page reads the run state back.
            logger.LogDebug(ex, "llmTest {Kind} for config {ConfigId} not delivered", outcome.Kind, outcome.ConfigId);
        }
    }
}
