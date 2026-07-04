using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// Real <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>. Captures
/// stdout and stderr interleaved into a single string; a timeout kills the process tree and throws.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"Process '{fileName} {arguments}' did not exit within {timeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString().TrimEnd());
        }
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sb)
        {
            sb.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort — the process may have exited between the check and the kill.
        }
    }
}
