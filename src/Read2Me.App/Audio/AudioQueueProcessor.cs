using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;

namespace Read2Me.App.Audio
{
    public sealed class AudioQueueProcessor(
        AudioQueueService queue,
        ParagraphTtsSettingsService ttsSettings,
        IParagraphTtsClientResolver ttsResolver,
        IBookCommandHandler commands,
        IFileSystem fs,
        IProjectDbContextFactory dbFactory,
        ILogger<AudioQueueProcessor> logger) : IAudioQueueProcessor
    {
        public async Task ProcessItemAsync(QueuedAudioItem queued, CancellationToken ct)
        {
            var (folder, itemRef) = (queued.Folder, queued.Item);
            queue.MarkProcessing(folder, itemRef);

            try
            {
                var folderPath = fs.GetProjectFolderPath(folder.Value);
                await using var db = await dbFactory.CreateAsync(folderPath);

                var row = await db.ParagraphItems
                    .AsNoTracking()
                    .Include(pi => pi.Character)
                    .FirstOrDefaultAsync(pi => pi.Id == itemRef.ParagraphItemId, ct);

                if (row is null)
                {
                    queue.MarkFailed(folder, itemRef, $"ParagraphItem {itemRef.ParagraphItemId} not found");
                    return;
                }

                var characterId = row.ItemType == ParagraphItemType.Narration
                    ? ProjectDbContext.NarratorId
                    : row.CharacterId;

                if (characterId is null)
                {
                    queue.MarkFailed(folder, itemRef, "No character assigned to item");
                    return;
                }

                var voice = await db.Voices
                    .AsNoTracking()
                    .Include(v => v.Character)
                    .FirstOrDefaultAsync(v => v.CharacterId == characterId && v.IsDefault, ct);

                if (voice is null)
                {
                    var charName = row.ItemType == ParagraphItemType.Narration
                        ? "Narrator"
                        : (row.Character?.Name ?? characterId.ToString());
                    queue.MarkFailed(folder, itemRef, $"No default voice for {charName}");
                    return;
                }

                var config = await ttsSettings.GetActiveConfigAsync();
                if (config is null)
                {
                    queue.MarkFailed(folder, itemRef, "No active TTS configuration");
                    return;
                }

                var client = ttsResolver.Resolve(config.Type);

                var refAudioPath = Path.Combine(folderPath, voice.AudioFileName!.Replace('/', Path.DirectorySeparatorChar));
                using var refAudio = fs.OpenRead(refAudioPath);

                var wavStream = await client.GenerateAsync(row.Text ?? string.Empty, row.VoiceInstructions, refAudio, config, ct);

                var relativePath = $"audio/{itemRef.ParagraphItemId}.wav";
                var audioFolder = Path.Combine(folderPath, "audio");
                fs.EnsureDirectory(audioFolder);
                var outPath = Path.Combine(audioFolder, $"{itemRef.ParagraphItemId}.wav");
                await fs.WriteFileAsync(outPath, wavStream);

                await commands.ExecuteAsync(
                    new SetParagraphItemAudioCommand(folder, itemRef.ParagraphItemId, relativePath), ct);

                queue.MarkComplete(folder, itemRef, relativePath);

                logger.LogInformation("Audio generated for item {ItemId}", itemRef.ParagraphItemId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Cancelled audio item {ItemId}", itemRef.ParagraphItemId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed processing audio item {ItemId}", itemRef.ParagraphItemId);
                queue.MarkFailed(folder, itemRef, ex.Message);
            }
        }
    }
}
