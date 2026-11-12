using Microsoft.Extensions.DependencyInjection;
using Read2Me.Services;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The ffmpeg paths both checks read. <see cref="Live"/>: what the app runs, the Settings value
    /// else <see cref="Configured"/> (null: PATH). <see cref="Configured"/>: config's
    /// <c>Ffmpeg:Path</c>. <see cref="Note"/>: set when a saved value hides a different configured one.
    /// </summary>
    internal sealed record FfmpegPaths(string? Live, string? Configured, string Shown, string? Note)
    {
        /// <summary>The settings audio generation reads, so the checks probe the ffmpeg the app runs.</summary>
        public static async Task<FfmpegPaths> ReadAsync(IServiceScopeFactory scopes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var settings = await scope.ServiceProvider.GetRequiredService<AudioProcessingSettingsService>().GetAsync();
            var live = settings.FfmpegPath;
            var configured = settings.ConfiguredFfmpegPath;
            var shown = live ?? "PATH";
            var note = configured is not null && !SamePath(configured, live)
                ? $"appsettings says {configured}; the app uses {shown} — change it in {SetupSettingsLink.AudioProcessing.Label}"
                : null;
            return new FfmpegPaths(live, configured, shown, note);
        }

        /// <summary>Paths that name one file however they're written (case, / or \, ..) are the same.</summary>
        private static bool SamePath(string a, string? b)
        {
            if (b is null)
                return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                return Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b)
                    && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
