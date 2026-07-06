using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Decorator over <see cref="IParagraphTtsClient"/> that splits a paragraph's text into
    /// sentences, packs them into chunks up to the provider's <c>MaxChunkChars</c>, synthesises
    /// each chunk with the inner client, and stitches the per-chunk WAVs with <c>ChunkPauseMs</c>
    /// of silence between them.
    ///
    /// Default path (SentenceSplitEnabled=false): chunking.
    /// Legacy path (SentenceSplitEnabled=true): per-sentence synthesis (dormant; preserved for
    /// rollback only — not the active code path).
    ///
    /// Single-chunk paragraphs return a byte-identical passthrough. If any chunk's inner call
    /// throws, the decorator throws — no partial audio is produced.
    /// </summary>
    public sealed class SentenceChunkedTtsClient(
        IParagraphTtsClient inner,
        AudioProcessingSettingsService settings,
        ILogger<SentenceChunkedTtsClient> logger) : IParagraphTtsClient
    {
        public async Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig config,
            string? settingsOverrideJson,
            string? referenceTranscript = null,
            CancellationToken ct = default)
        {
            var audioSettings = await settings.GetAsync();

            if (audioSettings.SentenceSplitEnabled)
            {
                // Legacy per-sentence path — dormant, kept for rollback.
                var sentences = SentenceSplitter.Split(text);
                var refBytesLegacy = await ReadAllAsync(referenceAudioStream, ct);
                var wavsLegacy = new List<Stream>(sentences.Count);
                foreach (var sentence in sentences)
                {
                    using var refCopy = new MemoryStream(refBytesLegacy, writable: false);
                    wavsLegacy.Add(await inner.GenerateAsync(sentence, voiceInstructions, refCopy, config, settingsOverrideJson, referenceTranscript, ct));
                }
                return WavStitcher.Stitch(wavsLegacy, audioSettings.ChunkPauseMs);
            }

            // Default: chunking path.
            var providerSettings = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson)
                ?? new VoxCpm2ParagraphTtsSettings();
            int maxChunkChars = providerSettings.MaxChunkChars;

            var splitSentences = SentenceSplitter.Split(text);
            var chunks = SentenceChunker.Chunk(splitSentences, maxChunkChars);

            logger.LogDebug("Chunking paragraph ({Chars} chars) into {Count} chunks (max {Max} chars each)",
                text.Length, chunks.Count, maxChunkChars);

            if (chunks.Count == 1)
            {
                logger.LogDebug("Single chunk ({Chars} chars) — passthrough: {Text}", chunks[0].Length, chunks[0]);
                return await inner.GenerateAsync(chunks[0], voiceInstructions, referenceAudioStream, config, settingsOverrideJson, referenceTranscript, ct);
            }

            for (int i = 0; i < chunks.Count; i++)
                logger.LogDebug("Chunk {Index}/{Count} ({Chars} chars): {Text}", i + 1, chunks.Count, chunks[i].Length, chunks[i]);

            var refBytes = await ReadAllAsync(referenceAudioStream, ct);
            var wavs = new List<Stream>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                using var refCopy = new MemoryStream(refBytes, writable: false);
                var wav = await inner.GenerateAsync(chunks[i], voiceInstructions, refCopy, config, settingsOverrideJson, referenceTranscript, ct);
                sw.Stop();
                logger.LogDebug(
                    "Chunk {Index}/{Count}: {Chars} chars synthesised in {Ms} ms",
                    i + 1, chunks.Count, chunks[i].Length, sw.ElapsedMilliseconds);
                wavs.Add(wav);
            }

            var stitched = WavStitcher.Stitch(wavs, audioSettings.ChunkPauseMs);
            logger.LogDebug(
                "Stitched {Count} chunks into {Bytes} bytes with {Pause} ms pause between chunks",
                chunks.Count, stitched.Length, audioSettings.ChunkPauseMs);
            return stitched;
        }

        private static async Task<byte[]> ReadAllAsync(Stream s, CancellationToken ct)
        {
            if (s is MemoryStream ms)
                return ms.ToArray();
            using var copy = new MemoryStream();
            await s.CopyToAsync(copy, ct);
            return copy.ToArray();
        }
    }
}
