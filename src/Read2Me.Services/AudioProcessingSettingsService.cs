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
        int ChunkPauseMs,
        int VolumePauseMs,
        int PartPauseMs,
        int ChapterPauseMs,
        int ParagraphPauseMs,
        int PauseMs);

    public class AudioProcessingSettingsService
    {
        public const double DefaultWerThreshold = 0.15;
        public const bool DefaultSentenceSplitEnabled = false;
        public const int DefaultChunkPauseMs = 300;
        public const int DefaultVolumePauseMs = 4000;
        public const int DefaultPartPauseMs = 3000;
        public const int DefaultChapterPauseMs = 2500;
        public const int DefaultParagraphPauseMs = 800;
        public const int DefaultPauseMs = 500;

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
                settings?.ChunkPauseMs ?? DefaultChunkPauseMs,
                settings?.VolumePauseMs ?? DefaultVolumePauseMs,
                settings?.PartPauseMs ?? DefaultPartPauseMs,
                settings?.ChapterPauseMs ?? DefaultChapterPauseMs,
                settings?.ParagraphPauseMs ?? DefaultParagraphPauseMs,
                settings?.PauseMs ?? DefaultPauseMs);
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

        /// <summary>Saves the pause between stitched audio chunks.</summary>
        public async Task SetChunkPauseAsync(int pauseMs)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.ChunkPauseMs = pauseMs);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Saves the per-kind pause durations used by the audiobook assembler.</summary>
        public async Task SetPauseDurationsAsync(int volumeMs, int partMs, int chapterMs, int paragraphMs, int pauseMs)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s =>
            {
                s.VolumePauseMs = volumeMs;
                s.PartPauseMs = partMs;
                s.ChapterPauseMs = chapterMs;
                s.ParagraphPauseMs = paragraphMs;
                s.PauseMs = pauseMs;
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
