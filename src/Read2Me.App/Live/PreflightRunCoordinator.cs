using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Read2Me.App.Services.Preflight;
using Read2Me.Services.Health;

namespace Read2Me.App.Live;

/// <summary>
/// Runs a pre-flight plan in the background — the request that started it has answered 202 with
/// the run id — and reports every service's stage to the caller's hub connection as
/// <c>preflight</c> messages, then <c>done</c>. The state machine is the same
/// <see cref="AiPreflightDialogPresenter"/> the Blazor dialog drives; lifecycle ops go through
/// <see cref="ObservedAiServiceControl"/> so every other client's chips follow too. Runs are
/// independent: the control facade serialises the docker work.
/// </summary>
public sealed class PreflightRunCoordinator(
    ObservedAiServiceControl control,
    IHubContext<LiveHub, ILiveClient> hub,
    ILogger<PreflightRunCoordinator> logger)
{
    /// <summary>Starts the run and returns its id; the messages carry it so a client can tell runs apart.</summary>
    public string Start(AiPreflightPlan plan, string? connectionId)
    {
        var run = Guid.NewGuid().ToString("N");
        _ = Task.Run(() => RunAsync(run, plan, connectionId), CancellationToken.None);
        return run;
    }

    private async Task RunAsync(string run, AiPreflightPlan plan, string? connectionId)
    {
        var client = connectionId is null ? null : hub.Clients.Client(connectionId);
        var presenter = new AiPreflightDialogPresenter(control);
        presenter.Load(plan);
        // The event fires synchronously inside RunAsync, where nothing can be awaited, so each
        // message is captured there and then sent on a chain that keeps the stages in sequence.
        var sends = Task.CompletedTask;
        presenter.StageChanged += row =>
        {
            var message = LiveMessageMapper.PreflightStage(run, row.Service.Name, StageName(row), row.Error);
            sends = sends.ContinueWith(_ => SendAsync(client, message), TaskScheduler.Default).Unwrap();
        };

        try
        {
            foreach (var row in presenter.Rows)
                await SendAsync(client, LiveMessageMapper.PreflightStage(run, row.Service.Name, StageName(row)));

            var ok = await presenter.RunAsync(CancellationToken.None);
            await sends;
            await SendAsync(client, LiveMessageMapper.PreflightDone(run, ok, ok ? null : presenter.FailureMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-flight run {Run} failed", run);
            await sends;
            await SendAsync(client, LiveMessageMapper.PreflightDone(run, false, ex.Message));
        }
    }

    /// <summary>The wire stage names the ticket fixes; Pending splits on what the row will do.</summary>
    public static string StageName(AiPreflightDialogPresenter.Row row) => row.Stage switch
    {
        AiPreflightDialogPresenter.ServiceStage.Pending => row.IsConflict ? "waitingToStop" : "waitingToStart",
        AiPreflightDialogPresenter.ServiceStage.Stopping => "stopping",
        AiPreflightDialogPresenter.ServiceStage.Stopped => "stopped",
        AiPreflightDialogPresenter.ServiceStage.Starting => "starting",
        AiPreflightDialogPresenter.ServiceStage.Ready => "ready",
        AiPreflightDialogPresenter.ServiceStage.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.Stage, "Unmapped preflight stage"),
    };

    private async Task SendAsync(ILiveClient? client, PreflightMessage message)
    {
        if (client is null)
            return;
        try
        {
            await client.Preflight(message);
        }
        catch (Exception ex)
        {
            // A closed connection is not the run's problem: the services still come up.
            logger.LogDebug(ex, "preflight {Kind} for run {Run} not delivered", message.Kind, message.Run);
        }
    }
}
