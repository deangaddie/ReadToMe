using System;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// Default <see cref="IAiServiceReporter"/>: resolves a base URL through the registry and forwards to
/// the health monitor. Stall-class failures (stalls and client timeouts) trip recovery immediately;
/// connection/other errors count toward the consecutive-failure threshold.
/// </summary>
public sealed class AiServiceReporter(DockerAiServiceRegistry registry, AiServiceHealthMonitor monitor)
    : IAiServiceReporter
{
    public void ReportSuccess(string baseUrl)
    {
        if (registry.TryGetByBaseUrl(baseUrl, out var service))
            monitor.ReportSuccess(service);
    }

    public bool ReportFailure(string baseUrl, Exception ex)
    {
        if (!registry.TryGetByBaseUrl(baseUrl, out var service))
            return false;

        monitor.ReportFailure(service, ex.Message, tripImmediately: IsStallClass(ex));
        return true;
    }

    /// <summary>Stalls and client timeouts trip immediately; connection-refused/socket errors count toward the threshold.</summary>
    public static bool IsStallClass(Exception ex) =>
        ex is AiServiceStalledException or TaskCanceledException or TimeoutException;
}
