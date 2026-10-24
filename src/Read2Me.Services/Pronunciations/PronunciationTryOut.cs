using System.IO;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Voice;

namespace Read2Me.Services.Pronunciations;

/// <summary>
/// One try-out: the sentence of item <paramref name="ItemId"/> that starts at <paramref name="SentenceStart"/>,
/// spoken with the list as <paramref name="Draft"/> would leave it (the list as it is when null), or as
/// written when <paramref name="AsWritten"/>, in voice <paramref name="VoiceId"/> or, when null, the
/// item's own resolved voice.
/// </summary>
public sealed record PronunciationTryRequest(
    Guid ItemId, int SentenceStart, PronunciationDraft? Draft, bool AsWritten, Guid? VoiceId);

/// <summary>Why a try-out rendered nothing.</summary>
public enum TryOutRefusal
{
    /// <summary>The draft is invalid, or no sentence starts where the request says.</summary>
    Invalid,
    /// <summary>The item, the voice or the drafted entry is not in the book.</summary>
    NotFound,
    /// <summary>Nothing to speak with: no voice resolves, the voice has no audio, or no TTS config is active.</summary>
    CannotSpeak,
}

public abstract record TryOutResult
{
    public sealed record Audio(byte[] Wav) : TryOutResult;

    public sealed record Refused(TryOutRefusal Reason, string Message) : TryOutResult;
}

/// <summary>
/// Renders a try-out sentence straight from TTS, as the item's own take would be asked for (same
/// voice resolution, active TTS config, reference transcript and delivery cue), loudness-normalised
/// so it sits level with the book's takes. Nothing is stored and no item's audio is touched. TTS
/// failures (<see cref="Audio.AudioCpp.TtsBusyException"/>, <see cref="Health.AiServiceUnavailableException"/>)
/// throw through for the caller to answer.
/// </summary>
public sealed class PronunciationTryOut(
    IFileSystem fs,
    ProjectDbSession session,
    IVoiceResolver voiceResolver,
    IParagraphTtsClientResolver ttsResolver,
    IAudioNormalizer normalizer,
    ParagraphTtsSettingsService ttsSettings,
    AudioProcessingSettingsService audioProcessingSettings,
    VoiceDesignSettingsService voiceDesignSettings)
{
    public async Task<TryOutResult> RenderAsync(ProjectFolderId folder, PronunciationTryRequest request, CancellationToken ct)
    {
        var db = await session.OpenAsync(folder);
        var item = await db.ParagraphItems.AsNoTracking()
            .Where(i => i.Id == request.ItemId)
            .Select(i => new { i.Text, i.VoiceInstructions })
            .SingleOrDefaultAsync(ct);
        if (item is null)
            return new TryOutResult.Refused(TryOutRefusal.NotFound, $"No item {request.ItemId}.");

        var list = await db.Pronunciations.AsNoTracking().ToListAsync(ct);
        IReadOnlyList<Data.Entities.Pronunciation> drafted;
        try
        {
            drafted = request.Draft is { } draft ? draft.ApplyTo(list).After : list;
        }
        catch (Mutations.BookMutationRejectedException refused)
        {
            return new TryOutResult.Refused(
                refused.Reason == Mutations.BookMutationRejection.NotFound ? TryOutRefusal.NotFound : TryOutRefusal.Invalid,
                refused.Message);
        }

        var matcher = new PronunciationMatcher(drafted);
        var text = item.Text ?? string.Empty;
        if (SentenceAt(text, request.SentenceStart, matcher) is not var (start, length))
            return new TryOutResult.Refused(TryOutRefusal.Invalid, $"No sentence starts at {request.SentenceStart} in that item.");
        var sentence = text.Substring(start, length);
        var spoken = request.AsWritten ? sentence : matcher.SpokenTextOf(sentence);

        var voiceId = request.VoiceId;
        if (voiceId is null)
        {
            (await voiceResolver.ResolveAsync(folder, [request.ItemId], ct)).TryGetValue(request.ItemId, out voiceId);
            if (voiceId is null)
                return new TryOutResult.Refused(TryOutRefusal.CannotSpeak, "No voice resolves for this item.");
        }
        var voice = await db.Voices.AsNoTracking().FirstOrDefaultAsync(v => v.Id == voiceId, ct);
        if (voice is null)
            return new TryOutResult.Refused(TryOutRefusal.NotFound, $"No voice {voiceId}.");
        if (string.IsNullOrEmpty(voice.AudioFileName))
            return new TryOutResult.Refused(TryOutRefusal.CannotSpeak, $"Voice '{voice.Name}' has no reference audio.");
        if (await ttsSettings.GetActiveConfigAsync() is not { } config)
            return new TryOutResult.Refused(TryOutRefusal.CannotSpeak, "No active TTS configuration.");

        var referenceTranscript = string.IsNullOrWhiteSpace(voice.Transcript)
            ? await voiceDesignSettings.GetSampleTextAsync()
            : voice.Transcript;
        var refAudioPath = Path.Combine(fs.GetProjectFolderPath(folder.Value),
            voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));

        await using var refAudio = fs.OpenRead(refAudioPath);
        await using var wav = await ttsResolver.Resolve(config.Type).GenerateAsync(
            AudioItemPipeline.ReplaceTrailingComma(spoken), item.VoiceInstructions, refAudio, config,
            voice.TtsSettingsOverrideJson, referenceTranscript, ct);

        // Normalised to sound as a take would; the raw output still plays when ffmpeg can't run.
        var raw = new MemoryStream();
        await wav.CopyToAsync(raw, ct);
        raw.Position = 0;
        var normalized = await normalizer.NormalizeAsync(raw, (await audioProcessingSettings.GetAsync()).FfmpegPath, ct);
        if (normalized.Status == NormalizeStatus.Normalized)
        {
            using var bytes = new MemoryStream();
            await normalized.Audio.CopyToAsync(bytes, ct);
            return new TryOutResult.Audio(bytes.ToArray());
        }
        return new TryOutResult.Audio(raw.ToArray());
    }

    /// <summary>
    /// The sentence starting at <paramref name="start"/>, carried on over any match that runs past its
    /// end, as the impact query's sentence around a match is; null when no sentence starts there.
    /// </summary>
    private static (int Start, int Length)? SentenceAt(string text, int start, PronunciationMatcher matcher)
    {
        var sentences = PronunciationImpact.SentencesOf(text);
        var at = sentences.FindIndex(s => s.Start == start);
        if (at < 0)
            return null;
        var end = sentences[at].Start + sentences[at].Length;
        foreach (var match in matcher.MatchesIn(text))
        {
            if (match.Start >= start && match.Start < end && match.Start + match.Length > end)
            {
                var around = PronunciationImpact.SentenceAround(sentences, match, text.Length);
                end = Math.Max(end, around.Start + around.Length);
            }
        }
        return (start, end - start);
    }
}
