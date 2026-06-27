using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.App.Services;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Events;

namespace Read2Me.App.Characters;

public sealed class VoiceBatchRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceBatchRunner> _logger;
    private readonly EventBroadcaster<VoiceBatchEvent> _batchEvents;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public int Processed { get; private set; }
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public string? CurrentVoiceName { get; private set; }
    public string? CurrentOperation { get; private set; }
    public string? LastError { get; private set; }

    public VoiceBatchRunner(
        ILogger<VoiceBatchRunner> logger,
        EventBroadcaster<VoiceBatchEvent> batchEvents)
        : this(scopeFactory: null!, logger, batchEvents) { }

    public VoiceBatchRunner(
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceBatchRunner> logger,
        EventBroadcaster<VoiceBatchEvent> batchEvents)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _batchEvents = batchEvents;
    }

    public bool StartGeneratePrompts(ProjectFolderId folder)
    {
        lock (_lock)
        {
            if (IsRunning) return false;
            ResetState("Generating prompts");
            var ct = _cts!.Token;
            Task.Run(() => RunWithScopeAsync(new GeneratePromptsPhase(), folder, ct));
        }
        return true;
    }

    public bool StartGenerateAudio(ProjectFolderId folder)
    {
        lock (_lock)
        {
            if (IsRunning) return false;
            ResetState("Generating audio");
            var ct = _cts!.Token;
            Task.Run(() => RunWithScopeAsync(new GenerateAudioPhase(), folder, ct));
        }
        return true;
    }

    public void Cancel()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    private void ResetState(string operation)
    {
        IsRunning = true;
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
        var deps = new PhaseDeps(
            scope.ServiceProvider.GetRequiredService<IProjectReader>(),
            scope.ServiceProvider.GetRequiredService<IBookCommandHandler>(),
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
        try
        {
            lock (_lock) { CurrentOperation = phase.Operation; }

            var workList = await phase.PlanAsync(deps, folder, ct);

            lock (_lock) { Total = workList.Count; }
            _batchEvents.Publish(new BatchStarted(phase.Operation, workList.Count));

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
            _batchEvents.Publish(new BatchCompleted(Processed - Failed, Failed));
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            _batchEvents.Publish(new BatchCancelled());
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
            _batchEvents.Publish(new BatchCompleted(Processed - Failed, Failed));
        }
    }
}
