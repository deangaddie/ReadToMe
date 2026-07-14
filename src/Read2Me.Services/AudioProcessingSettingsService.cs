using System.Text.Json;
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
        int PauseMs,
        int AudioMaxAttempts);

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
        public const int DefaultAudioMaxAttempts = 1;

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
                settings?.PauseMs ?? DefaultPauseMs,
                settings?.AudioMaxAttempts ?? DefaultAudioMaxAttempts);
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

        /// <summary>Saves the total audio-generation attempts per item. Values below 1 clamp to 1.</summary>
        public async Task SetAudioMaxAttemptsAsync(int maxAttempts)
        {
            var clamped = Math.Max(1, maxAttempts);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.AudioMaxAttempts = clamped);
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

        /// <summary>
        /// Reads the <b>paragraph</b> post-process step chain:
        /// <see cref="AudioPostProcessStepDefaults.For"/> with each step's stored enabled/settings
        /// merged on by id. Order and membership come from code, so a missing row, a null column, an
        /// absent id, or corrupt JSON all yield the step's default; ids in storage with no code
        /// default are ignored — which is also what keeps the Voice-only steps off the Audio
        /// Processing settings page and out of the paragraph pipeline.
        /// </summary>
        public virtual async Task<IReadOnlyList<AudioPostProcessStepConfig>> GetPostProcessStepsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return MergeOntoDefaults(DeserializeSteps(settings?.AudioPostProcessStepsJson));
        }

        /// <summary>Saves the post-process step list. Only enabled/settings survive a round-trip.</summary>
        public virtual async Task SetPostProcessStepsAsync(IReadOnlyList<AudioPostProcessStepConfig> steps)
        {
            var json = JsonSerializer.Serialize(steps, AudioPostProcessJson.Options);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.AudioPostProcessStepsJson = json);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Saves one step's config, keeping every other step's.</summary>
        public virtual async Task UpsertPostProcessStepAsync(AudioPostProcessStepConfig step)
        {
            var steps = (await GetPostProcessStepsAsync())
                .Select(s => s.StepId == step.StepId ? step : s)
                .ToList();
            await SetPostProcessStepsAsync(steps);
        }

        private static IReadOnlyList<AudioPostProcessStepConfig> MergeOntoDefaults(
            IReadOnlyList<AudioPostProcessStepConfig> stored)
        {
            var byId = stored
                .GroupBy(s => s.StepId)
                .ToDictionary(g => g.Key, g => g.First());

            return AudioPostProcessStepDefaults.For(StepScope.Paragraph)
                .Select(d => byId.TryGetValue(d.StepId, out var s)
                    ? d with { Enabled = s.Enabled, Settings = s.Settings ?? d.Settings }
                    : d)
                .ToList();
        }

        private IReadOnlyList<AudioPostProcessStepConfig> DeserializeSteps(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<AudioPostProcessStepConfig>>(json, AudioPostProcessJson.Options)
                    ?? [];
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Corrupt AudioPostProcessStepsJson; falling back to defaults");
                return [];
            }
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
