using System;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Health;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// UI-agnostic state and behaviour behind <c>DockerServiceControls</c>. Resolves a config's base
    /// URL to a managed Docker service (or nothing, for a remote endpoint), tracks the last known
    /// status, and drives start/restart/shutdown through <see cref="IAiServiceControl"/>. All logic
    /// here is testable with a fake facade — the razor component only maps this state to MudBlazor
    /// chrome and marshals <c>StateHasChanged</c>.
    /// </summary>
    public sealed class DockerServiceControlsPresenter(IAiServiceControl control)
    {
        /// <summary>Lifecycle actions the presenter can run; also selects the busy label.</summary>
        public enum Op { Start, Restart, Shutdown }

        /// <summary>The managed service, or null when the base URL is a remote endpoint (UI shows nothing).</summary>
        public DockerAiService? Service { get; private set; }

        /// <summary>Last fetched status; null until the first fetch completes.</summary>
        public AiServiceStatus? Status { get; private set; }

        /// <summary>True while a status fetch or lifecycle op is in flight — controls disable + spinner.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>Label of the running op ("Starting…" etc.), or null when idle.</summary>
        public string? BusyLabel { get; private set; }

        /// <summary>Whether this base URL maps to a managed container. When false the component renders nothing.</summary>
        public bool IsManaged => Service is not null;

        // Button visibility per status. Stopped/NotFound can only be started; a live-or-down container
        // can be restarted or shut down. Nothing is actionable while an op is in flight or status unknown.
        public bool CanStart => !IsBusy && Status is AiServiceStatus.Stopped or AiServiceStatus.NotFound;
        public bool CanRestart => !IsBusy && Status is AiServiceStatus.Ready or AiServiceStatus.Starting or AiServiceStatus.Down;
        public bool CanShutdown => CanRestart;
        public bool CanRefresh => !IsBusy;

        /// <summary>Resolve the base URL to a managed service. Returns true on a registry hit.</summary>
        public bool Resolve(string? baseUrl)
        {
            Service = string.IsNullOrWhiteSpace(baseUrl) ? null : control.Resolve(baseUrl);
            return IsManaged;
        }

        /// <summary>On-demand status fetch for this card's container only. No-op when unmanaged.</summary>
        public async Task RefreshStatusAsync(CancellationToken ct)
        {
            if (Service is null) return;

            IsBusy = true;
            BusyLabel = null;
            try
            {
                Status = await control.GetStatusAsync(Service, ct);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Run a lifecycle op, adopting the facade's resulting status (which the facade re-fetches on
        /// failure). The long start/restart warm-up is a single await here; the component fires it off
        /// the render and marshals completion back onto the circuit.
        /// </summary>
        public async Task<AiServiceOpResult> ExecuteAsync(Op op, CancellationToken ct)
        {
            if (Service is null)
                return new AiServiceOpResult(false, AiServiceStatus.Unknown, "No managed service.");

            IsBusy = true;
            BusyLabel = op switch
            {
                Op.Start => "Starting…",
                Op.Restart => "Restarting…",
                Op.Shutdown => "Shutting down…",
                _ => "Working…",
            };
            try
            {
                var result = op switch
                {
                    Op.Start => await control.StartAsync(Service, ct),
                    Op.Restart => await control.RestartAsync(Service, ct),
                    Op.Shutdown => await control.ShutdownAsync(Service, ct),
                    _ => throw new ArgumentOutOfRangeException(nameof(op)),
                };
                Status = result.Status;
                return result;
            }
            finally
            {
                IsBusy = false;
                BusyLabel = null;
            }
        }
    }
}
