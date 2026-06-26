using System.IO;
using Microsoft.EntityFrameworkCore;
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
        IProjectReader reader,
        ParagraphTtsSettingsService ttsSettings,
        AudioProcessingSettingsService audioProcessingSettings) : IAudioItemResolver
    {
        public async Task<ResolutionResult> ResolveAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var (folder, itemRef) = (queued.Folder, queued.Item);
            var folderPath = fs.GetProjectFolderPath(folder.Value);
            await using var db = await dbFactory.CreateAsync(folderPath);

            var row = await db.ParagraphItems
                .AsNoTracking()
                .Include(pi => pi.Character)
                .FirstOrDefaultAsync(pi => pi.Id == itemRef.ParagraphItemId, ct);

            if (row is null)
                return new ResolutionResult(null, null, null,
                    $"ParagraphItem {itemRef.ParagraphItemId} not found");

            var speaker = row.ItemType == ParagraphItemType.Narration
                ? "Narrator"
                : row.Character?.Name;

            var sourceText = row.Text ?? string.Empty;

            var narratorOnly = await db.Projects
                .AsNoTracking()
                .Select(p => p.NarratorOnlyMode)
                .FirstOrDefaultAsync(ct);

            var characterId = narratorOnly || row.ItemType == ParagraphItemType.Narration
                ? ProjectDbContext.NarratorId
                : row.CharacterId;

            if (characterId is null)
                return new ResolutionResult(speaker, sourceText, null,
                    "No character assigned to item");

            var (itemPos, ruleInputs) = await reader.GetVoiceRuleInputsAsync(folder, itemRef.ParagraphItemId, characterId.Value);
            var selectedVoiceId = VoiceRuleEvaluator.Evaluate(ruleInputs, itemPos);

            var voice = selectedVoiceId.HasValue
                ? await db.Voices
                    .AsNoTracking()
                    .Include(v => v.Character)
                    .FirstOrDefaultAsync(v => v.Id == selectedVoiceId.Value, ct)
                : null;

            if (voice is null)
            {
                var charName = row.ItemType == ParagraphItemType.Narration
                    ? "Narrator"
                    : (row.Character?.Name ?? characterId.ToString());
                return new ResolutionResult(speaker, sourceText, null,
                    $"No default voice for {charName}");
            }

            if (string.IsNullOrEmpty(voice.AudioFileName))
            {
                var charName = voice.Character?.Name ?? voice.Id.ToString();
                return new ResolutionResult(speaker, sourceText, null,
                    $"Voice '{charName}' has no reference audio");
            }

            var config = await ttsSettings.GetActiveConfigAsync();
            if (config is null)
                return new ResolutionResult(speaker, sourceText, null,
                    "No active TTS configuration");

            var refAudioPath = Path.Combine(folderPath, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));

            var processingSettings = await audioProcessingSettings.GetAsync();

            var request = new PipelineRequest(
                ParagraphItemId: itemRef.ParagraphItemId,
                SourceText: sourceText,
                Speaker: speaker,
                VoiceInstructions: row.VoiceInstructions,
                RefAudioPath: refAudioPath,
                TtsConfig: config,
                TtsSettingsOverrideJson: voice.TtsSettingsOverrideJson,
                MaxAttempts: Math.Max(1, processingSettings.AudioMaxAttempts),
                WerThreshold: processingSettings.WerThreshold,
                FfmpegPath: processingSettings.FfmpegPath);

            return new ResolutionResult(speaker, sourceText, request, null);
        }
    }
}
