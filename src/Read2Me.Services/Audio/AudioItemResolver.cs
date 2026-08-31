using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Voice;

namespace Read2Me.Services.Audio
{
    public sealed class AudioItemResolver(
        IFileSystem fs,
        IProjectDbContextFactory dbFactory,
        IVoiceResolver voiceResolver,
        ParagraphTtsSettingsService ttsSettings,
        AudioProcessingSettingsService audioProcessingSettings,
        VoiceDesignSettingsService voiceDesignSettings,
        ILogger<AudioItemResolver> logger) : IAudioItemResolver
    {
        public async Task<ResolutionResult> ResolveAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var (folder, itemRef) = (queued.Folder, queued.Item);
            logger.LogDebug("Item {Id} resolve start (folder {Folder})", itemRef.ParagraphItemId, folder.Value);
            var folderPath = fs.GetProjectFolderPath(folder.Value);
            await using var db = await dbFactory.CreateAsync(folderPath);

            var row = await db.ParagraphItems
                .AsNoTracking()
                .Include(pi => pi.Character)
                .FirstOrDefaultAsync(pi => pi.Id == itemRef.ParagraphItemId, ct);

            if (row is null)
                return new ResolutionResult(null, null, null,
                    $"ParagraphItem {itemRef.ParagraphItemId} not found");

            // Logs and the ItemStarted label name whoever actually speaks: under a narrator
            // link that is the linked character, unlinked it is the seed row's "Narrator".
            // Narration items only — this is the per-item path (one item, then seconds of TTS),
            // not VoiceResolver's batch path where the link has to ride an existing query.
            // Narration is the speaker, never the item type (ADR-0006).
            var isNarration = NarrationRule.IsNarration(row);
            var narratorName = isNarration
                ? (await NarratorIdentity.LoadAsync(db, ct)).DisplayName
                : null;

            var speaker = isNarration
                ? narratorName
                : row.Character?.Name;

            var sourceText = row.Text ?? string.Empty;

            var map = await voiceResolver.ResolveAsync(folder, [itemRef.ParagraphItemId], ct);
            map.TryGetValue(itemRef.ParagraphItemId, out var selectedVoiceId);

            if (selectedVoiceId is null)
            {
                // No speaker on a speech item → unattributed, not a missing-voice issue
                if (row.CharacterId is null && !ParagraphItemKinds.IsPause(row.ItemType))
                    return new ResolutionResult(speaker, sourceText, null,
                        "No character assigned to item");

                var charName = isNarration
                    ? narratorName!
                    : (row.Character?.Name ?? row.CharacterId?.ToString() ?? "unknown");
                return new ResolutionResult(speaker, sourceText, null,
                    $"No default voice for {charName}");
            }

            var voice = await db.Voices
                .AsNoTracking()
                .Include(v => v.Character)
                .FirstOrDefaultAsync(v => v.Id == selectedVoiceId.Value, ct);

            if (voice is null || string.IsNullOrEmpty(voice.AudioFileName))
            {
                var charName = voice?.Character?.Name ?? voice?.Id.ToString() ?? selectedVoiceId.Value.ToString();
                return new ResolutionResult(speaker, sourceText, null,
                    $"Voice '{charName}' has no reference audio");
            }

            var config = await ttsSettings.GetActiveConfigAsync();
            if (config is null)
                return new ResolutionResult(speaker, sourceText, null,
                    "No active TTS configuration");

            var refAudioPath = Path.Combine(folderPath, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));

            var processingSettings = await audioProcessingSettings.GetAsync();
            // Prefer the voice's own transcript — it matches the reference audio; the global
            // sample text only applies when the voice predates per-voice transcripts.
            var usingVoiceTranscript = !string.IsNullOrWhiteSpace(voice.Transcript);
            var referenceTranscript = usingVoiceTranscript
                ? voice.Transcript
                : await voiceDesignSettings.GetSampleTextAsync();

            logger.LogDebug(
                "Item {Id} resolved: speaker '{Speaker}', voice {VoiceId} ('{VoiceName}'), refAudio '{RefAudio}', " +
                "ttsConfig '{Config}' ({Type}), refTranscript from {TranscriptSource}, " +
                "maxAttempts {Max}, werThreshold {Wer}, overrides {HasOverrides}",
                itemRef.ParagraphItemId, speaker, voice.Id, voice.Character?.Name ?? "-", refAudioPath,
                config.Name, config.Type, usingVoiceTranscript ? "voice" : "global sample text",
                Math.Max(1, processingSettings.AudioMaxAttempts), processingSettings.WerThreshold,
                !string.IsNullOrWhiteSpace(voice.TtsSettingsOverrideJson));

            var request = new PipelineRequest(
                Folder: folder,
                ParagraphItemId: itemRef.ParagraphItemId,
                SourceText: sourceText,
                Speaker: speaker,
                VoiceInstructions: row.VoiceInstructions,
                RefAudioPath: refAudioPath,
                TtsConfig: config,
                TtsSettingsOverrideJson: voice.TtsSettingsOverrideJson,
                MaxAttempts: Math.Max(1, processingSettings.AudioMaxAttempts),
                WerThreshold: processingSettings.WerThreshold,
                FfmpegPath: processingSettings.FfmpegPath,
                ReferenceTranscript: referenceTranscript);

            return new ResolutionResult(speaker, sourceText, request, null);
        }
    }
}
