using System;

namespace Read2Me.Services.Health;

/// <summary>
/// Surfaced by an AI client when a request against a <em>managed</em> (registry-matched) service
/// failed and was reported to the watchdog. It lets the audio pipeline propagate a failure the
/// queue processor can distinguish from an ordinary error and requeue instead of fail.
/// </summary>
public sealed class AiServiceUnavailableException : Exception
{
    public string BaseUrl { get; }

    public AiServiceUnavailableException(string baseUrl, Exception inner)
        : base($"AI service at {baseUrl} is unavailable: {inner.Message}", inner)
    {
        BaseUrl = baseUrl;
    }
}
