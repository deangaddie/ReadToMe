using System;
using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// Tiny seam around launching an external process, so the container controller is unit-testable
/// without a real docker daemon. Mirrors existing seams like <c>IFfmpegProber</c>.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, returning the exit code and
    /// combined stdout/stderr. Kills the process and throws once <paramref name="timeout"/> elapses.
    /// A launch failure (e.g. executable not on PATH) surfaces as a thrown exception.
    /// </summary>
    Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}
