using System.Text;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Pure WAV trimmer for carrier-prefix TTS. Drops leading PCM up to a cut point and rebuilds
    /// a canonical RIFF/WAVE (fmt + data). Raw byte-splice — no ffmpeg. The RIFF chunks are walked
    /// to locate <c>fmt </c> and <c>data</c> (no fixed 44-byte header).
    /// </summary>
    public static class WavTrimmer
    {
        /// <summary>
        /// Returns a WAV with everything before <paramref name="cutSeconds"/> removed.
        /// The cut is aligned down to a whole sample frame and clamped to [0, duration].
        /// </summary>
        public static Stream TrimStart(Stream wav, double cutSeconds)
        {
            var parsed = Parse(ReadAll(wav));
            var fmt = parsed.Format;

            long frames = (long)(Math.Max(0, cutSeconds) * fmt.SampleRate);
            long cutBytes = frames * fmt.BlockAlign;
            if (cutBytes > parsed.Pcm.Length)
                cutBytes = parsed.Pcm.Length - parsed.Pcm.Length % fmt.BlockAlign;

            var pcm = new byte[parsed.Pcm.Length - cutBytes];
            Array.Copy(parsed.Pcm, cutBytes, pcm, 0, pcm.Length);

            return new MemoryStream(BuildWav(fmt, pcm), writable: false);
        }

        /// <summary>Cut is pulled back this far from the end of the silence, as a safety margin.</summary>
        private const double CutGuardSeconds = 0.010;

        /// <summary>A window counts as quiet when its RMS is within this factor of the quietest window.</summary>
        private const double QuietRmsFactor = 2.0;

        /// <summary>Absolute RMS floor, so digitally silent gaps (quietest RMS 0) still have a band.</summary>
        private const double QuietRmsFloor = 1.0;

        /// <summary>
        /// Finds where to cut between <paramref name="windowStartSec"/> and
        /// <paramref name="windowEndSec"/>: slides a ~10 ms RMS window over 16-bit PCM, takes the
        /// longest contiguous run of quiet windows (latest run wins ties) and returns the run's
        /// <em>end</em> less a small guard.
        ///
        /// The bias is deliberately late. The costs are asymmetric — extra leading silence is
        /// harmless, leftover carrier speech is not — and whisper's word-end timestamps run early,
        /// so the window typically opens inside the tail of the last carrier word. Cutting at the
        /// quietest single point would land in that tail; cutting at the end of the silence does not.
        /// Falls back to <paramref name="windowEndSec"/> less the guard when the audio is not 16-bit
        /// PCM or the window is too short to scan.
        /// </summary>
        public static double FindCarrierCut(Stream wav, double windowStartSec, double windowEndSec)
        {
            var parsed = Parse(ReadAll(wav));
            var fmt = parsed.Format;

            double duration = (double)(parsed.Pcm.Length / fmt.BlockAlign) / fmt.SampleRate;
            double start = Math.Clamp(windowStartSec, 0, duration);
            double end = Math.Clamp(windowEndSec, 0, duration);
            if (end < start)
                (start, end) = (end, start);
            double lateFallback = Math.Max(start, end - CutGuardSeconds);

            short bitsPerSample = BitConverter.ToInt16(fmt.FmtChunkBody, 14);
            if (bitsPerSample != 16 || end - start < 0.02)
                return lateFallback;

            int channels = fmt.BlockAlign / 2;
            long startFrame = (long)(start * fmt.SampleRate);
            long endFrame = (long)(end * fmt.SampleRate);
            long windowFrames = fmt.SampleRate / 100; // ~10 ms
            if (windowFrames < 1 || endFrame - startFrame < windowFrames)
                return lateFallback;

            long stepFrames = Math.Max(1, windowFrames / 4);

            var windowFrame = new List<long>();
            var windowRms = new List<double>();
            for (long frame = startFrame; frame + windowFrames <= endFrame; frame += stepFrames)
            {
                double energy = 0;
                for (long f = frame; f < frame + windowFrames; f++)
                {
                    long byteOffset = f * fmt.BlockAlign;
                    for (int c = 0; c < channels; c++)
                    {
                        double sample = BitConverter.ToInt16(parsed.Pcm, (int)(byteOffset + c * 2));
                        energy += sample * sample;
                    }
                }

                windowFrame.Add(frame);
                windowRms.Add(Math.Sqrt(energy / (windowFrames * channels)));
            }

            if (windowRms.Count == 0)
                return lateFallback;

            // Quiet band is relative to the quietest window, so a noisy gap still registers.
            double threshold = Math.Max(windowRms.Min() * QuietRmsFactor, QuietRmsFloor);

            // Longest contiguous quiet run; >= keeps the latest run when lengths tie.
            int bestEnd = -1;
            int bestLength = 0;
            int runStart = -1;
            for (int i = 0; i < windowRms.Count; i++)
            {
                if (windowRms[i] > threshold)
                {
                    runStart = -1;
                    continue;
                }

                if (runStart < 0)
                    runStart = i;

                int length = i - runStart + 1;
                if (length >= bestLength)
                {
                    bestLength = length;
                    bestEnd = i;
                }
            }

            if (bestEnd < 0)
                return lateFallback; // unreachable: the quietest window always clears the threshold

            double runEnd = (double)(windowFrame[bestEnd] + windowFrames) / fmt.SampleRate;
            return Math.Clamp(runEnd - CutGuardSeconds, start, end);
        }

        private static byte[] ReadAll(Stream s)
        {
            if (s.CanSeek)
                s.Position = 0;
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

        /// <summary>Rebuilds a canonical RIFF/WAVE (fmt + data) around the trimmed PCM.</summary>
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
