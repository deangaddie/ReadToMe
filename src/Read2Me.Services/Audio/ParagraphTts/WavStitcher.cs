using System.Text;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Pure WAV stitcher for sentence-chunked TTS. Concatenates the PCM <c>data</c> of an ordered
    /// list of per-sentence WAV streams, inserting zero-PCM silence between adjacent sentences only
    /// (N chunks → N−1 gaps; no leading or trailing silence). Raw byte-splice — no ffmpeg.
    /// The RIFF chunks are walked to locate <c>fmt </c> and <c>data</c> (no fixed 44-byte header).
    /// </summary>
    public static class WavStitcher
    {
        public static Stream Stitch(IReadOnlyList<Stream> chunks, int pauseMs)
        {
            if (chunks.Count == 0)
                throw new ArgumentException("At least one chunk is required.", nameof(chunks));

            // One chunk is a byte-identical passthrough — no parsing, no silence.
            if (chunks.Count == 1)
                return new MemoryStream(ReadAll(chunks[0]), writable: false);

            var parsed = new List<WavData>(chunks.Count);
            foreach (var chunk in chunks)
                parsed.Add(Parse(ReadAll(chunk)));

            // Format is uniform across chunks by construction; the first chunk's format drives silence.
            var fmt = parsed[0].Format;
            int silenceBytes = SilenceBytes(fmt, pauseMs);
            var silence = new byte[silenceBytes];

            using var data = new MemoryStream();
            for (int i = 0; i < parsed.Count; i++)
            {
                if (i > 0)
                    data.Write(silence, 0, silence.Length);
                data.Write(parsed[i].Pcm, 0, parsed[i].Pcm.Length);
            }

            return new MemoryStream(BuildWav(fmt, data.ToArray()), writable: false);
        }

        /// <summary>Silence byte count for <paramref name="pauseMs"/>, aligned to a whole sample frame.</summary>
        private static int SilenceBytes(WavFormat fmt, int pauseMs)
        {
            long samples = (long)fmt.SampleRate * pauseMs / 1000;
            return (int)(samples * fmt.BlockAlign);
        }

        private static byte[] ReadAll(Stream s)
        {
            if (s is MemoryStream ms)
                return ms.ToArray();
            using var copy = new MemoryStream();
            s.CopyTo(copy);
            return copy.ToArray();
        }

        private readonly record struct WavFormat(int SampleRate, short BlockAlign, byte[] FmtChunkBody);

        private readonly record struct WavData(WavFormat Format, byte[] Pcm);

        /// <summary>Walks the RIFF chunks to extract the fmt body and the PCM data segment.</summary>
        private static WavData Parse(byte[] wav)
        {
            if (wav.Length < 12 || Ascii(wav, 0, 4) != "RIFF" || Ascii(wav, 8, 4) != "WAVE")
                throw new InvalidOperationException("Not a RIFF/WAVE stream.");

            byte[]? fmtBody = null;
            byte[]? pcm = null;

            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = Ascii(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                int body = pos + 8;

                if (id == "fmt ")
                {
                    fmtBody = new byte[size];
                    Array.Copy(wav, body, fmtBody, 0, size);
                }
                else if (id == "data")
                {
                    pcm = new byte[size];
                    Array.Copy(wav, body, pcm, 0, size);
                }

                pos = body + size + (size & 1); // chunks are word-aligned
            }

            if (fmtBody is null || pcm is null)
                throw new InvalidOperationException("WAV missing fmt or data chunk.");

            int sampleRate = BitConverter.ToInt32(fmtBody, 4);
            short blockAlign = BitConverter.ToInt16(fmtBody, 12);
            return new WavData(new WavFormat(sampleRate, blockAlign, fmtBody), pcm);
        }

        /// <summary>Rebuilds a canonical RIFF/WAVE (fmt + data) around the concatenated PCM.</summary>
        private static byte[] BuildWav(WavFormat fmt, byte[] pcm)
        {
            int fmtSize = fmt.FmtChunkBody.Length;
            int riffSize = 4 + (8 + fmtSize) + (8 + pcm.Length);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(riffSize);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));

            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(fmtSize);
            w.Write(fmt.FmtChunkBody);

            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);

            w.Flush();
            return ms.ToArray();
        }

        private static string Ascii(byte[] b, int offset, int len) =>
            Encoding.ASCII.GetString(b, offset, len);
    }
}
