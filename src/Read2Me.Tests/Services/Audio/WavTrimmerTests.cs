using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WavTrimmerTests
    {
        private const int SampleRate = 16000;

        private static byte[] BuildWav(
            byte[] pcm,
            short channels = 1,
            short bitsPerSample = 16,
            bool extraChunkBeforeData = false)
        {
            short blockAlign = (short)(channels * bitsPerSample / 8);
            int byteRate = SampleRate * blockAlign;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            byte[] extra = extraChunkBeforeData ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>();
            int extraChunkTotal = extraChunkBeforeData ? 8 + extra.Length : 0;

            int riffSize = 4 + (8 + 16) + extraChunkTotal + (8 + pcm.Length);

            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(riffSize);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write(channels);
            w.Write(SampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bitsPerSample);

            if (extraChunkBeforeData)
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));
                w.Write(extra.Length);
                w.Write(extra);
            }

            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);

            w.Flush();
            return ms.ToArray();
        }

        /// <summary>16-bit mono PCM from per-sample amplitudes, each held for a whole run of frames.</summary>
        private static byte[] Pcm16(params (short Amplitude, int Frames)[] runs)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            foreach (var (amplitude, frames) in runs)
                for (int i = 0; i < frames; i++)
                    w.Write(amplitude);
            w.Flush();
            return ms.ToArray();
        }

        private static byte[] ReadData(byte[] wav)
        {
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "data")
                {
                    var pcm = new byte[size];
                    Array.Copy(wav, pos + 8, pcm, 0, size);
                    return pcm;
                }
                pos += 8 + size + (size & 1);
            }
            throw new InvalidOperationException("no data chunk");
        }

        private static byte[] TrimStart(byte[] wav, double cutSeconds)
        {
            using var result = WavTrimmer.TrimStart(new MemoryStream(wav), cutSeconds);
            using var ms = new MemoryStream();
            result.CopyTo(ms);
            return ms.ToArray();
        }

        [Fact]
        public void TrimStart_RemovesLeadingFrames()
        {
            // 1 second at 0x1111 then 1 second at 0x2222; cut half a second in.
            var pcm = Pcm16(((short)0x1111, SampleRate), ((short)0x2222, SampleRate));
            var wav = BuildWav(pcm);

            var data = ReadData(TrimStart(wav, 0.5));

            Assert.Equal((SampleRate + SampleRate / 2) * 2, data.Length);
            Assert.Equal(0x1111, BitConverter.ToInt16(data, 0));
            Assert.Equal(0x2222, BitConverter.ToInt16(data, data.Length - 2));
        }

        [Fact]
        public void TrimStart_ZeroCut_KeepsAllPcm()
        {
            var pcm = Pcm16(((short)0x1234, 100));
            var wav = BuildWav(pcm);

            var data = ReadData(TrimStart(wav, 0));

            Assert.Equal(pcm, data);
        }

        [Fact]
        public void TrimStart_CutPastEnd_ReturnsEmptyData()
        {
            var pcm = Pcm16(((short)0x1234, 100));
            var wav = BuildWav(pcm);

            var data = ReadData(TrimStart(wav, 60));

            Assert.Empty(data);
        }

        [Fact]
        public void TrimStart_NegativeCut_KeepsAllPcm()
        {
            var pcm = Pcm16(((short)0x1234, 100));
            var wav = BuildWav(pcm);

            var data = ReadData(TrimStart(wav, -1));

            Assert.Equal(pcm, data);
        }

        [Fact]
        public void TrimStart_Stereo_CutsWholeFrames()
        {
            // Stereo: each frame is 4 bytes. 100 frames, cut 25 frames' worth of time.
            var pcm = new byte[100 * 4];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (byte)(i % 251);
            var wav = BuildWav(pcm, channels: 2);

            var data = ReadData(TrimStart(wav, 25.0 / SampleRate));

            Assert.Equal(75 * 4, data.Length);
            Assert.Equal(pcm[25 * 4], data[0]); // starts exactly at frame 25
        }

        [Fact]
        public void TrimStart_DataChunkNotAtFixed44Offset_IsParsed()
        {
            var pcm = Pcm16(((short)0x1111, 100));
            var wav = BuildWav(pcm, extraChunkBeforeData: true);

            var data = ReadData(TrimStart(wav, 0));

            Assert.Equal(pcm, data);
        }

        [Fact]
        public void TrimStart_Output_HasValidRiffHeader()
        {
            var pcm = Pcm16(((short)0x1111, 200));
            var wav = BuildWav(pcm);

            var result = TrimStart(wav, 100.0 / SampleRate);

            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(result, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(result, 8, 4));
            Assert.Equal(result.Length - 8, BitConverter.ToInt32(result, 4));
        }

        [Fact]
        public void FindQuietestCut_ReturnsCentreOfSilentGap()
        {
            // Loud 0.5 s, silent 0.1 s, loud 0.5 s. Search across the whole middle area.
            var pcm = Pcm16(
                ((short)20000, SampleRate / 2),
                ((short)0, SampleRate / 10),
                ((short)20000, SampleRate / 2));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindQuietestCut(new MemoryStream(wav), 0.3, 0.8);

            Assert.InRange(cut, 0.5, 0.6); // inside the silent gap
        }

        [Fact]
        public void FindQuietestCut_WindowShorterThan20Ms_ReturnsMidpoint()
        {
            var pcm = Pcm16(((short)20000, SampleRate));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindQuietestCut(new MemoryStream(wav), 0.400, 0.410);

            Assert.Equal(0.405, cut, precision: 6);
        }

        [Fact]
        public void FindQuietestCut_Non16Bit_ReturnsMidpoint()
        {
            // 8-bit PCM: RMS scan unsupported, midpoint fallback.
            var pcm = new byte[SampleRate]; // 1 second of 8-bit mono
            var wav = BuildWav(pcm, channels: 1, bitsPerSample: 8);

            double cut = WavTrimmer.FindQuietestCut(new MemoryStream(wav), 0.2, 0.6);

            Assert.Equal(0.4, cut, precision: 6);
        }

        [Fact]
        public void FindQuietestCut_ClampsWindowToDuration()
        {
            var pcm = Pcm16(((short)20000, SampleRate / 2)); // 0.5 s total
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindQuietestCut(new MemoryStream(wav), 0.4, 2.0);

            Assert.InRange(cut, 0.4, 0.5);
        }

        [Fact]
        public void FindQuietestCut_Stereo_FindsSilentGap()
        {
            // Stereo loud/silent/loud.
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            void WriteFrames(short amplitude, int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    w.Write(amplitude);
                    w.Write(amplitude);
                }
            }
            WriteFrames(20000, SampleRate / 2);
            WriteFrames(0, SampleRate / 10);
            WriteFrames(20000, SampleRate / 2);
            w.Flush();
            var wav = BuildWav(ms.ToArray(), channels: 2);

            double cut = WavTrimmer.FindQuietestCut(new MemoryStream(wav), 0.3, 0.8);

            Assert.InRange(cut, 0.5, 0.6);
        }
    }
}
