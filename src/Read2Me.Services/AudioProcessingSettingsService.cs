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
    /// <summary>
    /// Snapshot of the audio post-processing settings row, with defaults already applied for
    /// a missing row.
    /// </summary>
    public readonly record struct AudioProcessingSettings(
        string? FfmpegPath,
        double WerThreshold,
        bool SentenceSplitEnabled,
        int SentencePauseMs,
        int SentenceMinChunkChars);

    public class AudioProcessingSettingsService
    {
        public const double DefaultWerThreshold = 0.15;
        public const bool DefaultSentenceSplitEnabled = true;
        public const int DefaultSentencePauseMs = 300;
        public const int DefaultSentenceMinChunkChars = 15;

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

        public virtual async Task<AudioProcessingSettings> GetAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return new AudioProcessingSettings(
                settings?.FfmpegPath,
                settings?.WerThreshold ?? DefaultWerThreshold,
                settings?.SentenceSplitEnabled ?? DefaultSentenceSplitEnabled,
                settings?.SentencePauseMs ?? DefaultSentencePauseMs,
                settings?.SentenceMinChunkChars ?? DefaultSentenceMinChunkChars);
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

        /// <summary>Saves the sentence-chunking settings (toggle, pause, min-merge length).</summary>
        public async Task SetSentenceChunkingAsync(bool enabled, int pauseMs, int minChunkChars)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s =>
            {
                s.SentenceSplitEnabled = enabled;
                s.SentencePauseMs = pauseMs;
                s.SentenceMinChunkChars = minChunkChars;
            });
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Probes ffmpeg using the currently saved path.</summary>
        public async Task<FfmpegProbeResult> TestFfmpegAsync(CancellationToken ct = default)
        {
            var settings = await GetAsync();
            return await _prober.ProbeAsync(settings.FfmpegPath, ct);
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
