using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Events;
using Read2Me.Services.Health;

namespace Read2Me.App.Live;

/// <summary>
/// Runs a manual Start / Restart / Shutdown of one managed container in the background — the
/// request that asked has answered 202 by the time docker is spoken to, since a start can take
/// minutes of health polling and warm-up. Goes through <see cref="ObservedAiServiceControl"/>, so
/// the outcome reaches every live client as a <c>serviceStatus</c> message carrying the op and its
/// result; nothing is sent to the caller specifically. One op per service at a time.
/// </summary>
public sealed class AiServiceOpCoordinator(
    ObservedAiServiceControl control,
    EventBroadcaster<ServiceStatusChanged> events,
    ILogger<AiServiceOpCoordinator> logger)
{
    public const string Start = "start";
    public const string Restart = "restart";
    public const string Shutdown = "shutdown";

    private readonly ConcurrentDictionary<string, string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Services with an op in flight, with the op's name.</summary>
    public IReadOnlyDictionary<string, string> InFlight => _inFlight;

    /// <summary>False when the service already has an op in flight.</summary>
    public bool TryStart(DockerAiService service, string op)
    {
        if (!_inFlight.TryAdd(service.Name, op))
            return false;

        _ = Task.Run(() => RunAsync(service, op), CancellationToken.None);
        return true;
    }

    private async Task RunAsync(DockerAiService service, string op)
    {
        try
        {
            var result = op switch
            {
                Start => await control.StartAsync(service, CancellationToken.None),
                Restart => await control.RestartAsync(service, CancellationToken.None),
                Shutdown => await control.ShutdownAsync(service, CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown service op"),
            };
            if (!result.Succeeded)
                logger.LogWarning("Manual {Op} of {Service} failed: {Error}", op, service.Name, result.Error);
        }
        catch (Exception ex)
        {
            // The facade reports failures as results; only a bug reaches here. Published anyway so
            // a client's busy state never hangs on an op that vanished.
            logger.LogError(ex, "Manual {Op} of {Service} threw", op, service.Name);
            events.Publish(new ServiceStatusChanged(service.Name, AiServiceStatus.Unknown, op, false, ex.Message));
        }
        finally
        {
            _inFlight.TryRemove(service.Name, out _);
        }
    }
}
