using System;

namespace Read2Me.Services.Health;

/// <summary>
/// The two-line seam AI clients use to report request outcomes to the watchdog without taking a
/// registry + monitor dependency pair. A base URL that resolves to no managed service (a remote
/// endpoint) is silently ignored.
/// </summary>
public interface IAiServiceReporter
{
    /// <summary>A request against <paramref name="baseUrl"/> succeeded; clears the failure streak if managed.</summary>
    void ReportSuccess(string baseUrl);

    /// <summary>
    /// A request against <paramref name="baseUrl"/> failed. Returns true if the URL matched a managed
    /// service (and was therefore reported), so the caller can surface an
    /// <see cref="AiServiceUnavailableException"/>; false for a remote miss.
    /// </summary>
    bool ReportFailure(string baseUrl, Exception ex);
}
