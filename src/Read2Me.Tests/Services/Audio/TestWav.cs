using Read2Me.Services.Audio;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>Canonical WAV (24 kHz mono 16-bit) fixtures for the ffmpeg-gated step tests.</summary>
    internal static class TestWav
    {
        /// <summary>A loud 440 Hz tone — loud enough that peak regrowth is observable.</summary>
        public static byte[] Tone(int ms, double amplitude = 0.8)
        {
            var samples = new short[CanonicalWav.SampleRateHz * ms / 1000];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)(short.MaxValue * amplitude *
                    Math.Sin(2 * Math.PI * 440 * i / CanonicalWav.SampleRateHz));

            return Write(samples);
        }

        /// <summary>Peak sample as a fraction of full scale.</summary>
        public static double PeakAmplitude(byte[] wav)
        {
            var peak = 0;
            for (var i = CanonicalWav.HeaderBytes; i + 1 < wav.Length; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(wav, i)));

            return peak / (double)short.MaxValue;
        }

        private static byte[] Write(short[] samples)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var dataBytes = samples.Length * CanonicalWav.BytesPerSample;

            bw.Write("RIFF"u8.ToArray());
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);                              // PCM
            bw.Write((short)1);                              // mono
            bw.Write(CanonicalWav.SampleRateHz);
            bw.Write(CanonicalWav.BytesPerSecond);
            bw.Write((short)CanonicalWav.BytesPerSample);
            bw.Write((short)16);
            bw.Write("data"u8.ToArray());
            bw.Write(dataBytes);
            foreach (var s in samples) bw.Write(s);

            bw.Flush();
            return ms.ToArray();
        }
    }
}
