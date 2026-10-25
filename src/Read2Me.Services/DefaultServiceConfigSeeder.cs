using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Audio.Transcription.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services
{
    /// <summary>
    /// First-run setup: a brand-new app database gets one service config per AI container in
    /// <c>Infra/docker-compose.yml</c>, each pointed at its default localhost port and made active, so
    /// a fresh install works as soon as the containers are up. The values mirror the tuned
    /// development workspace (llama presets in <c>Infra/llama/config/models.ini</c>, audio.cpp model
    /// ids in <c>Infra/audiocpp/server.json</c>). Adds no configs to a database that already holds one.
    /// It also stores the host's <c>Ffmpeg:Path</c>, so a fresh tool workspace finds ffmpeg with no
    /// manual step; only call it for a new database, since that write ignores existing configs.
    /// </summary>
    public static class DefaultServiceConfigSeeder
    {
        public const string LlamaUrl = "http://localhost:8080";
        public const string AudioCppUrl = "http://localhost:8004";
        public const string WhisperUrl = "http://localhost:9000";
        public const string MpnetUrl = "http://localhost:8201";

        /// <param name="ffmpegPath">Stored as <see cref="AudioProcessingSettingsService.SetFfmpegPathAsync"/>
        /// would store it, whether or not configs already existed; blank or null leaves the stored path
        /// as it is (null on a new database, meaning ffmpeg on PATH).</param>
        /// <returns>True when the default configs were written; false when any config already existed.</returns>
        public static async Task<bool> SeedAsync(Read2MeDbContext db, string? ffmpegPath = null, CancellationToken ct = default)
        {
            var seeded = await SeedConfigsAsync(db, ct);
            var normalized = AudioProcessingSettingsService.NormalizeFfmpegPath(ffmpegPath);
            if (normalized != null)
            {
                var settings = await GetOrAddSettingsAsync(db, ct);
                settings.FfmpegPath = normalized;
                await db.SaveChangesAsync(ct);
            }
            return seeded;
        }

        private static async Task<bool> SeedConfigsAsync(Read2MeDbContext db, CancellationToken ct)
        {
            if (await db.LlmServerConfigs.AnyAsync(ct)
                || await db.ParagraphTtsServiceConfigs.AnyAsync(ct)
                || await db.VoiceDesignServiceConfigs.AnyAsync(ct)
                || await db.TranscriptionServiceConfigs.AnyAsync(ct)
                || await db.SemanticSimilarityServiceConfigs.AnyAsync(ct))
                return false;

            // Infra/README.md "Attribution preset": qwen-28b with thinking off is the active config.
            var qwenAttribution = new LlmServerConfig
            {
                Name = "Qwen 28b – attribution",
                BaseUrl = LlamaUrl,
                Model = "qwen-28b",
                Temperature = 0.7,
                TopP = 0.8,
                MaxTokens = 8192,
                PresencePenalty = 1.5,
                AttributionBatchSize = 4,
                PromptStyle = AttributionPromptStyle.Full,
                SupportsModelSwitch = true,
            };
            var qwenThinking = new LlmServerConfig
            {
                Name = "Qwen 28b – thinking",
                BaseUrl = LlamaUrl,
                Model = "qwen-28b",
                Temperature = 1.0,
                TopP = 0.95,
                MaxTokens = 8192,
                PresencePenalty = 1.5,
                AttributionBatchSize = 4,
                PromptStyle = AttributionPromptStyle.Full,
                SupportsModelSwitch = true,
            };
            // Infra/README.md "Chapter-pass preset" (ADR 0013): the chapter pass samples greedily and
            // without thinking whatever the config says, so it leaves sampling to the server.
            var gemmaChapter = new LlmServerConfig
            {
                Name = "Gemma 12b – Chapter attribution",
                BaseUrl = LlamaUrl,
                Model = "gemma-12b",
                AttributionBatchSize = 1,
                PromptStyle = AttributionPromptStyle.Chapter,
                SupportsModelSwitch = true,
            };
            db.LlmServerConfigs.AddRange(qwenAttribution, qwenThinking, gemmaChapter);

            var breezeTts = NewParagraphTts("Breeze (audio.cpp)", ParagraphTtsServiceType.Breeze,
                BreezeParagraphTtsSettings.Recommended with { BaseUrl = AudioCppUrl });
            var voxCpm2Tts = NewParagraphTts("VoxCPM2 (audio.cpp)", ParagraphTtsServiceType.VoxCpm2,
                VoxCpm2ParagraphTtsSettings.Recommended with
                {
                    BaseUrl = AudioCppUrl,
                    MaxChunkChars = 1500,
                    CarrierPrefixEnabled = true,
                    CarrierMaxTargetChars = 20,
                });
            db.ParagraphTtsServiceConfigs.AddRange(breezeTts, voxCpm2Tts);

            var breezeDesign = new VoiceDesignServiceConfig
            {
                Name = "Breeze (audio.cpp)",
                Type = VoiceDesignServiceType.Breeze,
                SettingsJson = JsonSerializer.Serialize(
                    BreezeVoiceDesignSettings.Recommended with { BaseUrl = AudioCppUrl }),
            };
            var voxCpm2Design = new VoiceDesignServiceConfig
            {
                Name = "VoxCPM2 (audio.cpp)",
                Type = VoiceDesignServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(
                    VoxCpm2VoiceDesignSettings.Recommended with { BaseUrl = AudioCppUrl }),
            };
            db.VoiceDesignServiceConfigs.AddRange(breezeDesign, voxCpm2Design);

            var whisper = new TranscriptionServiceConfig
            {
                Name = "Whisper",
                Type = TranscriptionServiceType.LocalWhisper,
                SettingsJson = JsonSerializer.Serialize(new LocalWhisperSettings { BaseUrl = WhisperUrl }),
            };
            db.TranscriptionServiceConfigs.Add(whisper);

            var mpnet = new SemanticSimilarityServiceConfig
            {
                Name = "MPNet-Base-v2",
                Type = SemanticSimilarityServiceType.MpnetBaseV2,
                SettingsJson = JsonSerializer.Serialize(new SemanticSimilaritySettings { BaseUrl = MpnetUrl }),
            };
            db.SemanticSimilarityServiceConfigs.Add(mpnet);

            await db.SaveChangesAsync(ct);

            var settings = await GetOrAddSettingsAsync(db, ct);
            settings.ActiveLlmConfigId = qwenAttribution.Id;
            settings.ActiveParagraphTtsConfigId = breezeTts.Id;
            settings.ActiveVoiceDesignConfigId = breezeDesign.Id;
            settings.ActiveTranscriptionConfigId = whisper.Id;
            settings.ActiveSemanticConfigId = mpnet.Id;
            settings.AttributionChainIdsJson = JsonSerializer.Serialize(new[]
            {
                new AttributionChainEntry(gemmaChapter.Id, Thinking: false, AttributionPromptStyle.Chapter),
            });
            await db.SaveChangesAsync(ct);
            return true;
        }

        private static async Task<AppSettings> GetOrAddSettingsAsync(Read2MeDbContext db, CancellationToken ct)
        {
            var settings = await db.Settings.SingleOrDefaultAsync(ct);
            if (settings == null)
            {
                settings = new AppSettings();
                db.Settings.Add(settings);
            }
            return settings;
        }

        /// <summary>
        /// A paragraph-TTS config with the standard text clean-up: de-shout all-caps, turn brackets
        /// into commas, and normalise spaced or triple-dot ellipses to a single ellipsis character.
        /// </summary>
        private static ParagraphTtsServiceConfig NewParagraphTts<TSettings>(
            string name, ParagraphTtsServiceType type, TSettings settings)
        {
            var steps = new List<TextSubstitutionStep>
            {
                new() { FromText = "(", ToText = ",", Order = 0 },
                new() { FromText = ")", ToText = ",", Order = 1 },
                new() { FromText = ". . .", ToText = "…", Order = 2 },
                new() { FromText = "...", ToText = "…", Order = 3 },
            };
            return new ParagraphTtsServiceConfig
            {
                Name = name,
                Type = type,
                SettingsJson = JsonSerializer.Serialize(settings),
                SubstitutionSteps = steps,
                EnabledStepIds = ["to-sentence-case", .. steps.Select(s => s.Id)],
                ToSentenceCaseConfig = new ToSentenceCaseConfig
                {
                    ParagraphEnabled = true,
                    WordEnabled = true,
                    WordMinLength = 5,
                },
            };
        }
    }
}
