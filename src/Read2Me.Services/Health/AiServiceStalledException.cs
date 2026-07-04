using System;

namespace Read2Me.Services.Health;

/// <summary>
/// Thrown when a streaming AI request goes silent — no chunk for the configured inactivity window —
/// so a wedged container is aborted instead of waited on forever. Carries the service base URL and
/// the elapsed inactivity so callers can resolve the registry entry and report the stall.
/// </summary>
public sealed class AiServiceStalledException : Exception
{
    public string BaseUrl { get; }
    public TimeSpan Elapsed { get; }

    public AiServiceStalledException(string baseUrl, TimeSpan elapsed)
        : base($"AI service at {baseUrl} stalled: no response for {elapsed.TotalSeconds:F0}s")
    {
        BaseUrl = baseUrl;
        Elapsed = elapsed;
    }
}
