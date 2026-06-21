using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Decorator over <see cref="IParagraphTtsClient"/> that splits an item's text into sentences,
    /// synthesises each sentence with the inner client, and stitches the per-sentence WAVs into one
    /// with a configurable pause between sentences. Self-gating: when <c>SentenceSplitEnabled</c> is
    /// off it forwards the original text to the inner client unchanged. A single-sentence item makes
    /// one inner call and returns a byte-identical passthrough. If any sentence's inner call throws,
    /// the decorator throws — no partial audio is produced.
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
            CancellationToken ct = default)
        {
            var chunking = await settings.GetAsync();

            if (!chunking.SentenceSplitEnabled)
                return await inner.GenerateAsync(text, voiceInstructions, referenceAudioStream, config, ct);

            var chunks = SentenceSplitter.Split(text, chunking.SentenceMinChunkChars);

            // Buffer the reference audio once so each sentence gets a fresh, rewound stream.
            var refBytes = await ReadAllAsync(referenceAudioStream, ct);

            var wavs = new List<Stream>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                using var refCopy = new MemoryStream(refBytes, writable: false);
                var wav = await inner.GenerateAsync(chunks[i], voiceInstructions, refCopy, config, ct);
                sw.Stop();
                logger.LogDebug(
                    "Sentence chunk {Index}/{Count}: {Chars} chars synthesised in {Ms} ms",
                    i + 1, chunks.Count, chunks[i].Length, sw.ElapsedMilliseconds);
                wavs.Add(wav);
            }

            var stitched = WavStitcher.Stitch(wavs, chunking.SentencePauseMs);
            logger.LogDebug(
                "Stitched {Count} chunks into {Bytes} bytes with {Pause} ms pause between sentences",
                chunks.Count, stitched.Length, chunking.SentencePauseMs);
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
