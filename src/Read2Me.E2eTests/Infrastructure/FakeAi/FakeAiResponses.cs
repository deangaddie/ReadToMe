using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Wire-format builders for the fake AI endpoints. Shapes mirror what the real
/// clients parse: OpenAI SSE chunks (OpenAiStreamParser), VoxCPM2 binary frames
/// (VoxCpm2ParagraphTtsClient), Whisper.CPP verbose JSON, similarity JSON.
/// </summary>
public static partial class FakeAiResponses
{
    /// <summary>SSE body streaming <paramref name="content"/> as a single delta chunk, then [DONE].</summary>
    public static string OpenAiSse(string content)
    {
        var chunk = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content } } },
        });
        return $"data: {chunk}\n\ndata: [DONE]\n\n";
    }

    /// <summary>Whisper.CPP <c>response_format=verbose_json</c> response for transcript verification.</summary>
    public static string WhisperVerboseJson(string text) => JsonSerializer.Serialize(new { text });

    /// <summary>
    /// Per-item attribution answer for the paragraph(s) the given prompt asks about: every item the
    /// prompt numbered is answered with <paramref name="character"/>. Boundaries are frozen
    /// (ADR 0005), so the answer echoes no text at all — only the indices lifted from the prompt's
    /// own item list ("query" for single, "paragraphs" entries with an "index" for batch). Answers
    /// on narration indices are ignored by the apply, so naming every index is safe and keeps this
    /// fake free of the prompt's item types.
    /// </summary>
    public static string AttributionReply(string prompt, string character, string voiceInstructions = "calm")
    {
        var batch = BatchTargets().Matches(prompt);
        if (batch.Count > 0)
            return "[" + string.Join(",", batch.Select(m =>
                $$"""{ "index": {{m.Groups[1].Value}}, "reasoning": "fake", "items": {{AnsweredItems(m.Groups[2].Value, character, voiceInstructions)}} }""")) + "]";

        var query = QueryItems().Match(prompt);
        var items = query.Success ? query.Groups[1].Value : string.Empty;
        return $$"""{ "reasoning": "fake", "items": {{AnsweredItems(items, character, voiceInstructions)}} }""";
    }

    /// <summary>
    /// One answer per index in the prompt's "items" array for a query paragraph, all naming the same
    /// speaker.
    /// </summary>
    private static string AnsweredItems(string itemsJson, string speaker, string voiceInstructions) =>
        "[" + string.Join(",", ItemIndex().Matches(itemsJson).Select(m =>
            $$"""{ "index": {{m.Groups[1].Value}}, "speaker": "{{speaker}}", "voice_instructions": "{{voiceInstructions}}" }""")) + "]";

    // A batch target paragraph: its "index" followed by its own "items" array. Item objects carry
    // an "index" too, but never one followed by "items", so they cannot match here.
    [GeneratedRegex(@"""index""\s*:\s*(\d+)\s*,\s*""items""\s*:\s*\[([^\]]*)\]")]
    private static partial Regex BatchTargets();

    [GeneratedRegex(@"""query""\s*:\s*\{\s*""items""\s*:\s*\[([^\]]*)\]")]
    private static partial Regex QueryItems();

    [GeneratedRegex(@"""index""\s*:\s*(\d+)")]
    private static partial Regex ItemIndex();

    /// <summary>
    /// VoxCPM2 /api/stream response: meta frame, one float32 PCM frame (100ms of silence),
    /// done frame. Frame = [type:1 byte][len:4 bytes LE][payload].
    /// </summary>
    public static byte[] VoxCpm2StreamFrames(int sampleRate = 16000)
    {
        var ms = new MemoryStream();
        WriteFrame(ms, 0, JsonSerializer.SerializeToUtf8Bytes(new { type = "meta", sample_rate = sampleRate }));
        WriteFrame(ms, 1, new byte[sampleRate / 10 * 4]); // 100ms of float32 zeros
        WriteFrame(ms, 0, JsonSerializer.SerializeToUtf8Bytes(new { type = "done" }));
        return ms.ToArray();
    }

    /// <summary>Minimal valid 16-bit PCM mono WAV with 100ms of silence.</summary>
    public static byte[] SilentWav(int sampleRate = 16000)
    {
        var samples = sampleRate / 10;
        var dataLen = samples * 2;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        w.Write(new byte[dataLen]);
        return ms.ToArray();
    }

    private static void WriteFrame(Stream s, byte type, byte[] payload)
    {
        s.WriteByte(type);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)payload.Length);
        s.Write(len);
        s.Write(payload);
    }
}
