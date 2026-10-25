using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Read2Me.App.Configuration;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// The two things every post-process step test needs: a path that is certainly not ffmpeg (which
    /// exercises the never-throw fallback without touching the disk), and the real ffmpeg the
    /// real-ffmpeg tests run against.
    /// </summary>
    internal static class TestFfmpeg
    {
        private const string NotFoundMessage =
            "ffmpeg not found: set Ffmpeg:Path in appsettings.Local.json";

        // Resolved once per test run: probing spawns a process.
        private static readonly Lazy<string?> Resolved = new(Resolve);

        public static string BogusPath() =>
            Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

        /// <summary>
        /// The ffmpeg to run: <c>Ffmpeg:Path</c> from the bin's <c>appsettings.Local.json</c> (copied
        /// from the App) or the <c>Ffmpeg__Path</c> environment variable, otherwise <c>ffmpeg</c> on
        /// PATH. Skips the calling test when that doesn't answer <c>-version</c>.
        /// </summary>
        public static string Require()
        {
            var path = Resolved.Value;
            if (path is null)
                Assert.Skip(NotFoundMessage);
            return path;
        }

        private static string? Resolve()
        {
            var configured = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(LocalSettings.FileName, optional: true)
                .AddEnvironmentVariables()
                .Build()["Ffmpeg:Path"];
            var path = string.IsNullOrWhiteSpace(configured) ? "ffmpeg" : configured.Trim();
            return Answers(path) ? path : null;
        }

        private static bool Answers(string path)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (p is null) return false;
                // Drain the banner so a full pipe can't stall ffmpeg past the timeout.
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(3000))
                {
                    p.Kill(entireProcessTree: true);
                    return false;
                }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
