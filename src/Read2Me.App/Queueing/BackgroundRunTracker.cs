using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Read2Me.App.Characters;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Queueing;

/// <summary>
/// Tracks each project's <b>Background run</b> for History: tells <see cref="BackgroundRuns"/> when
/// the Character Queue or the voice batch makes a project busy, and when each stops — drained, or
/// ended early with how far it got ("212 of 340 paragraphs, cancelled", "3 of 7 voices, stopped on an error"),
/// and how many of its items failed on their own ("3 paragraphs failed").
/// <para>
/// On app start, before following anything, it closes every step a previous run of the app left
/// open as "interrupted": nothing that opened one is running any more.
/// </para>
/// </summary>
public sealed class BackgroundRunTracker(
    CharacterQueueService characterQueue,
    VoiceBatchRunner voiceBatch,
    EventBroadcaster<VoiceBatchEvent> batchEvents,
    BackgroundRuns runs,
    ProjectHistory history,
    IFileSystem fs,
    ILogger<BackgroundRunTracker> logger) : IHostedService
{
    private readonly object _lock = new();
    private readonly Dictionary<ProjectFolderId, Attribution> _attribution = [];
    private readonly List<Task> _closing = [];
    private ProjectFolderId? _batchFolder;

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var folder in fs.ListProjectFolders())
        {
            try
            {
                await history.CloseInterruptedAsync(new ProjectFolderId(folder), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Closing the History steps {Folder} left open failed; they stay open.", folder);
            }
        }

        Follow();
    }

    /// <summary>Stops following, then lets closes already under way finish, so a run's ending is not lost to shutdown.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        characterQueue.Changed -= OnQueueChanged;
        characterQueue.Cancelling -= OnQueueCancelling;
        batchEvents.Event -= OnBatchEvent;
        return ClosedAsync().WaitAsync(ct);
    }

    /// <summary>Starts following the queue and the voice batch. <see cref="StartAsync"/> does this once it has closed what was left open.</summary>
    internal void Follow()
    {
        characterQueue.Changed += OnQueueChanged;
        characterQueue.Cancelling += OnQueueCancelling;
        batchEvents.Event += OnBatchEvent;
    }

    /// <summary>Completes once every run step this tracker has closed so far is closed in History.</summary>
    internal Task ClosedAsync()
    {
        lock (_lock)
            return Task.WhenAll(_closing);
    }

    /// <summary>
    /// A folder with paragraphs pending starts a run; one whose last paragraph has settled ends it.
    /// Every paragraph seen pending counts toward the run, and one no longer pending is done. The
    /// paragraphs that failed are counted as the folder drains, before anything clears their outcomes.
    /// </summary>
    private void OnQueueChanged()
    {
        lock (_lock)
        {
            // Read under the lock, so a snapshot taken before another thread's is never applied after it.
            var pending = characterQueue.PendingParagraphs();
            foreach (var paragraphs in pending)
            {
                if (!_attribution.TryGetValue(paragraphs.Key, out var run))
                {
                    _attribution[paragraphs.Key] = run = new Attribution();
                    runs.Started(paragraphs.Key, BackgroundRunKind.Attribution);
                }
                if (run.Cancelled)
                    continue; // how far it got was fixed when the cancel began
                run.Seen.UnionWith(paragraphs);
                run.Done = run.Seen.Count - paragraphs.Count();
            }

            foreach (var (folder, run) in _attribution.Where(r => !pending.Contains(r.Key)).ToList())
            {
                _attribution.Remove(folder);
                if (!run.Cancelled)
                    run.Failed = FailedIn(folder, run);
                Close(runs.EndedAsync(folder, BackgroundRunKind.Attribution, StepNotes.Ending(
                    run.Done, run.Seen.Count, "paragraph", run.Cancelled ? StepNotes.Cancelled : null, run.Failed)));
            }
        }
    }

    /// <summary>
    /// Raised before the queue clears: every folder's run is marked cancelled, with how far it got
    /// and how many paragraphs failed — the clear takes their outcomes — and ends on the
    /// <see cref="CharacterQueueService.Changed"/> that follows the clear. Until then work still in
    /// flight commits into it.
    /// </summary>
    private void OnQueueCancelling()
    {
        lock (_lock)
        {
            foreach (var (folder, run) in _attribution)
            {
                run.Cancelled = true;
                run.Failed = FailedIn(folder, run);
            }
        }
    }

    private int FailedIn(ProjectFolderId folder, Attribution run) =>
        run.Seen.Count(p => characterQueue.OutcomeOf(folder, p)?.Kind == ParagraphOutcomeKind.Failed);

    /// <summary>
    /// The batch runs from its first event (once planning has counted the work) to its terminal one.
    /// A batch whose planning failed never started, so it has no run to end.
    /// </summary>
    private void OnBatchEvent(VoiceBatchEvent e)
    {
        lock (_lock)
        {
            switch (e)
            {
                case BatchStarted when voiceBatch.Folder is { } folder:
                    _batchFolder = folder;
                    runs.Started(folder, BackgroundRunKind.VoiceBatch);
                    break;

                case BatchCancelled or BatchCompleted when _batchFolder is { } folder:
                    _batchFolder = null;
                    Close(runs.EndedAsync(folder, BackgroundRunKind.VoiceBatch, VoiceBatchEnding(
                        e is BatchCancelled, voiceBatch.LastError is not null, voiceBatch.Processed, voiceBatch.Total, voiceBatch.Failed)));
                    break;
            }
        }
    }

    /// <summary>
    /// How a voice batch ended: "3 of 7 voices, cancelled" or "3 of 7 voices, stopped on an error",
    /// either way with the voices that failed on their own before it ("· 2 failed"), or just those
    /// ("2 voices failed") for one that ran to the end; null for one that ran to the end clean.
    /// </summary>
    internal static string? VoiceBatchEnding(bool cancelled, bool stoppedOnError, int processed, int total, int failed)
    {
        var how = cancelled ? StepNotes.Cancelled : stoppedOnError ? StepNotes.StoppedOnError : null;
        return StepNotes.Ending(processed, total, "voice", how, failed);
    }

    /// <summary>Closing waits for the project's write lock, so it runs on without holding up the queue or the batch.</summary>
    private void Close(Task closing)
    {
        _closing.RemoveAll(t => t.IsCompleted);
        _closing.Add(LoggedAsync(closing));
    }

    private async Task LoggedAsync(Task closing)
    {
        try
        {
            await closing;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Closing a Background run step failed; the next commit closes it.");
        }
    }

    private sealed class Attribution
    {
        public HashSet<Guid> Seen { get; } = [];
        public int Done { get; set; }
        public bool Cancelled { get; set; }
        public int Failed { get; set; }
    }
}
