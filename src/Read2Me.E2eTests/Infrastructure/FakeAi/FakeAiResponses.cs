using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Wire-format builders for the fake AI endpoints. Shapes mirror what the real
/// clients parse: OpenAI SSE chunks (OpenAiStreamParser), VoxCPM2 binary frames
/// (VoxCpm2ParagraphTtsClient), whisper plain-text, similarity JSON.
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

    public static string OpenAiModels() =>
        JsonSerializer.Serialize(new { data = new[] { new { id = "fake-model" } } });

    /// <summary>
    /// Segment-list attribution answer for the paragraph(s) the given prompt asks about: one dialog
    /// segment per paragraph, speaking its whole text. The text has to be echoed back verbatim — the
    /// service validates that the segments reconstruct the original paragraph — so it is lifted
    /// straight out of the prompt's context JSON ("query" for single, "paragraphs" for batch).
    /// </summary>
    public static string AttributionReply(string prompt, string character, string voiceInstructions = "calm")
    {
        var batch = BatchTargets().Matches(prompt);
        if (batch.Count > 0)
            return "[" + string.Join(",", batch.Select(m =>
                $$"""{ "index": {{m.Groups[1].Value}}, "reasoning": "fake", "segments": [ {{Segment(m.Groups[2].Value, character, voiceInstructions)}} ] }""")) + "]";

        var query = QueryText().Match(prompt);
        var text = query.Success ? query.Groups[1].Value : "\"\"";
        return $$"""{ "reasoning": "fake", "segments": [ {{Segment(text, character, voiceInstructions)}} ] }""";
    }

    /// <param name="jsonText">A JSON string literal (quoted, already escaped) lifted from the prompt.</param>
    private static string Segment(string jsonText, string speaker, string voiceInstructions) =>
        $$"""{ "text": {{jsonText}}, "type": "dialog", "speaker": "{{speaker}}", "voice_instructions": "{{voiceInstructions}}" }""";

    [GeneratedRegex(@"""index""\s*:\s*(\d+)\s*,\s*""text""\s*:\s*(""(?:[^""\\]|\\.)*"")")]
    private static partial Regex BatchTargets();

    [GeneratedRegex(@"""query""\s*:\s*\{\s*""text""\s*:\s*(""(?:[^""\\]|\\.)*"")")]
    private static partial Regex QueryText();

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
