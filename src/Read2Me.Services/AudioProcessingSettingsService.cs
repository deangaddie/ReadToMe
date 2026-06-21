using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;

namespace Read2Me.Services
{
    /// <summary>
    /// Single-row settings for the audio post-processing pipeline: the ffmpeg executable
    /// path and the WER pass threshold. Mirrors the single-row upsert pattern used by
    /// <see cref="ThemeService"/> / <see cref="VoiceDesignSettingsService"/>.
    /// </summary>
    public class AudioProcessingSettingsService
    {
        public const double DefaultWerThreshold = 0.15;

        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly IFfmpegProber _prober;
        private readonly ILogger<AudioProcessingSettingsService> _logger;

        public event Action? OnChanged;

        public AudioProcessingSettingsService(
            IDbContextFactory<Read2MeDbContext> dbFactory,
            IFfmpegProber prober,
            ILogger<AudioProcessingSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _prober = prober;
            _logger = logger;
        }

        public virtual async Task<(string? FfmpegPath, double WerThreshold)> GetAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return (settings?.FfmpegPath, settings?.WerThreshold ?? DefaultWerThreshold);
        }

        /// <summary>Saves the ffmpeg path. Blank/whitespace is stored as null (rely on PATH).</summary>
        public async Task SetFfmpegPathAsync(string? ffmpegPath)
        {
            var normalized = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath.Trim();
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.FfmpegPath = normalized);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Saves the WER pass threshold.</summary>
        public async Task SetWerThresholdAsync(double werThreshold)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.WerThreshold = werThreshold);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Probes ffmpeg using the currently saved path.</summary>
        public async Task<FfmpegProbeResult> TestFfmpegAsync(CancellationToken ct = default)
        {
            var (ffmpegPath, _) = await GetAsync();
            return await _prober.ProbeAsync(ffmpegPath, ct);
        }

        private static async Task MutateSettingsAsync(Read2MeDbContext db, Action<AppSettings> mutate)
        {
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null)
            {
                settings = new AppSettings();
                db.Settings.Add(settings);
            }
            mutate(settings);
        }
    }
}
