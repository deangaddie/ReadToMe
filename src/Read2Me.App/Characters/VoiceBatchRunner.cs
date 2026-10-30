using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.App.Services;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Llm;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Characters;

/// <summary>Whether a voice batch started, or why not.</summary>
public abstract record VoiceBatchStart
{
    private VoiceBatchStart() { }

    /// <summary>The batch is running in the background.</summary>
    public sealed record Started : VoiceBatchStart;

    /// <summary>A batch is already running; the runner runs one at a time, whatever the project.</summary>
    public sealed record AlreadyRunning : VoiceBatchStart;

    /// <summary>The <see cref="ProjectActivity"/> gate refused the enqueue.</summary>
    public sealed record Refused(ActivityRefusal Refusal) : VoiceBatchStart;
}

public sealed class VoiceBatchRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProjectActivity _activity;
    private readonly ILogger<VoiceBatchRunner> _logger;
    private readonly EventBroadcaster<VoiceBatchEvent> _batchEvents;
    private readonly EventBroadcaster<LlmStreamEvent> _llmEvents;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public int Processed { get; private set; }
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public string? CurrentVoiceName { get; private set; }
    public string? CurrentOperation { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The project of the running (or most recent) batch; null until the first start.</summary>
    public ProjectFolderId? Folder { get; private set; }

    public VoiceBatchRunner(
        IServiceScopeFactory scopeFactory,
        ProjectActivity activity,
        ILogger<VoiceBatchRunner> logger,
        EventBroadcaster<VoiceBatchEvent> batchEvents,
        EventBroadcaster<LlmStreamEvent> llmEvents)
    {
        _scopeFactory = scopeFactory;
        _activity = activity;
        _logger = logger;
        _batchEvents = batchEvents;
        _llmEvents = llmEvents;
    }

    public VoiceBatchStart StartGeneratePrompts(ProjectFolderId folder, bool regenerateAll = false) =>
        Start("Generating prompts", folder, ct => RunWithScopeAsync(new GeneratePromptsPhase(regenerateAll), folder, ct));

    public VoiceBatchStart StartGenerateAudio(ProjectFolderId folder) =>
        Start("Generating audio", folder, ct => RunWithScopeAsync(new GenerateAudioPhase(), folder, ct));

    /// <summary>
    /// Starts the batch unless one is running or the <see cref="ProjectActivity"/> gate refuses.
    /// <see cref="ProjectActivity.MayEnqueue"/> is asked under the lock <see cref="BusyFor"/> takes,
    /// so it can't race a Restore: one that claimed the project first is seen here, and one that
    /// claims it after finds this batch running and is refused.
    /// </summary>
    private VoiceBatchStart Start(string operation, ProjectFolderId folder, Func<CancellationToken, Task> run)
    {
        lock (_lock)
        {
            if (IsRunning) return new VoiceBatchStart.AlreadyRunning();
            if (_activity.MayEnqueue(folder) is { } refusal) return new VoiceBatchStart.Refused(refusal);
            ResetState(operation, folder);
            var ct = _cts!.Token;
            Task.Run(() => run(ct));
        }
        return new VoiceBatchStart.Started();
    }

    public void Cancel()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    /// <summary>
    /// The batch running for <paramref name="folder"/>, or null when none is. Reads "Voice batch:
    /// generating prompts, 7 voices left" once planning has counted the work.
    /// </summary>
    public BusySource? BusyFor(ProjectFolderId folder)
    {
        lock (_lock)
        {
            if (!IsRunning || Folder != folder) return null;
            var operation = (CurrentOperation ?? "running").ToLowerInvariant();
            var reason = Total > 0
                ? $"Voice batch: {operation}, {BusySource.Plural(Total - Processed, "voice")} left"
                : $"Voice batch: {operation}";
            return new BusySource(BusySourceKind.VoiceBatch, reason);
        }
    }

    private void ResetState(string operation, ProjectFolderId folder)
    {
        IsRunning = true;
        Folder = folder;
        Processed = 0;
        Total = 0;
        Failed = 0;
        CurrentVoiceName = null;
        CurrentOperation = operation;
        LastError = null;
        _cts = new CancellationTokenSource();
    }

    private async Task RunWithScopeAsync<TWork>(ISweepPhase<TWork> phase, ProjectFolderId folder, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        // What the batch commits is its project's Background run step in History.
        scope.ServiceProvider.GetRequiredService<HistoryStepScope>().JoinBackgroundRuns(BackgroundRunKind.VoiceBatch);
        var deps = new PhaseDeps(
            scope.ServiceProvider.GetRequiredService<IProjectReader>(),
            scope.ServiceProvider.GetRequiredService<BookMutations>(),
            scope.ServiceProvider.GetRequiredService<IVoiceAudioRemover>(),
            scope.ServiceProvider.GetRequiredService<VoiceOrchestrator>());
        await RunPhaseAsync(phase, deps, folder, ct);
    }

    /// Pure envelope: takes already-built deps, creates no scope. Unit-under-test.
    public async Task RunPhaseAsync<TWork>(
        ISweepPhase<TWork> phase,
        PhaseDeps deps,
        ProjectFolderId folder,
        CancellationToken ct)
    {
        // A batch of N is ONE Throughput Run, bracketing the whole foreach at exactly the
        // BatchStarted/BatchCompleted points. The flag keeps the finally honest: if planning
        // throws there is no run to end, and every other exit — completion, cancellation,
        // failure — must close the run or the next run's total is stranded.
        var runStarted = false;
        VoiceBatchEvent? terminalEvent = null;
        try
        {
            lock (_lock) { CurrentOperation = phase.Operation; }

            var workList = await phase.PlanAsync(deps, folder, ct);

            lock (_lock) { Total = workList.Count; }
            _batchEvents.Publish(new BatchStarted(phase.Operation, workList.Count));
            if (phase.DrivesLlm)
            {
                _llmEvents.Publish(new RunStarted());
                runStarted = true;
            }

            foreach (var item in workList)
            {
                ct.ThrowIfCancellationRequested();

                lock (_lock) { CurrentVoiceName = phase.DisplayName(item); }
                _batchEvents.Publish(new BatchProgress(Processed, Total, Failed, phase.DisplayName(item)));

                PhaseStepOutcome outcome;
                try
                {
                    outcome = await phase.RunStepAsync(item, deps, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Step failed for item in phase {Operation}", phase.Operation);
                    lock (_lock) { Failed++; Processed++; }
                    continue;
                }

                if (outcome.Ok)
                {
                    lock (_lock) { Processed++; }
                    if (outcome.Update is not null)
                        _batchEvents.Publish(outcome.Update);
                }
                else
                {
                    _logger.LogWarning("Soft-fail in phase {Operation}: {Reason}", phase.Operation, outcome.FailReason);
                    lock (_lock) { Failed++; Processed++; }
                }
            }

            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            terminalEvent = new BatchCompleted(Processed - Failed, Failed);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            terminalEvent = new BatchCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VoiceBatchRunner phase {Operation} failed", phase.Operation);
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
                LastError = ex.Message;
            }
            terminalEvent = new BatchCompleted(Processed - Failed, Failed);
        }
        finally
        {
            // Close the Throughput Run before the ordinary terminal batch event repaints the
            // progress UI. The batch dialog can then pull the final snapshot and reveal its table
            // without subscribing to the LLM stream or adding a throughput-specific repaint.
            if (runStarted)
                _llmEvents.Publish(new RunEnded());
            if (terminalEvent is not null)
                _batchEvents.Publish(terminalEvent);
        }
    }
}
