using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Services.BookEdits;

namespace Read2Me.App.Live;

/// <summary>
/// Runs a session's proposal in the background — the request that started it has answered 202
/// by the time the first batch returns — and reports to the caller's hub connection. Each run
/// opens its own DI scope because the proposal service is scoped and the request scope is gone.
/// </summary>
public sealed class BookEditRunCoordinator(
    IServiceScopeFactory scopes,
    IHubContext<LiveHub, ILiveClient> hub,
    ILogger<BookEditRunCoordinator> logger)
{
    /// <summary>False when the session already has a run in flight.</summary>
    public bool TryStart(BookEditSession session, bool thinking, string? connectionId)
    {
        if (!session.TryBeginRun(out var ct))
            return false;

        _ = Task.Run(() => RunAsync(session, thinking, connectionId, ct), CancellationToken.None);
        return true;
    }

    private async Task RunAsync(BookEditSession session, bool thinking, string? connectionId, CancellationToken ct)
    {
        var total = session.Targets.Count;
        var client = connectionId is null ? null : hub.Clients.Client(connectionId);
        // Not System.Progress<T>: that posts to a context and could land after "done". The send is
        // deliberately not awaited — a slow client must not pace the run.
        var progress = new SyncProgress((done, reported) =>
        {
            session.ReportProgress(done);
            _ = SendAsync(client, LiveMessageMapper.BookEditProgress(session.Id, done, reported));
        });

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var proposals = scope.ServiceProvider.GetRequiredService<BookEditProposalService>();
            var rows = await proposals.ProposeAsync(
                session.Folder, session.Program, session.Targets, progress, !thinking, ct);
            // The service swallows cancellation and answers the rows it had; a cancel that lands
            // between batches is only visible on the token.
            var cancelled = ct.IsCancellationRequested;
            session.CompleteRun(rows, cancelled);
            await SendAsync(client, LiveMessageMapper.BookEditDone(session.Id, rows, total, cancelled));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.CompleteRun([], cancelled: true);
            await SendAsync(client, LiveMessageMapper.BookEditDone(session.Id, [], total, cancelled: true));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Book edit proposal run {Program} failed", session.Id);
            session.FailRun(ex.Message);
            await SendAsync(client, LiveMessageMapper.BookEditFailed(session.Id, ex.Message));
        }
    }

    private sealed class SyncProgress(Action<int, int> report) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => report(value.Done, value.Total);
    }

    private async Task SendAsync(ILiveClient? client, BookEditMessage message)
    {
        if (client is null)
            return;
        try
        {
            await client.BookEdit(message);
        }
        catch (Exception ex)
        {
            // A closed connection is not the run's problem: the rows are on the session.
            logger.LogDebug(ex, "bookEdit {Kind} for {Program} not delivered", message.Kind, message.Program);
        }
    }
}
