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
        public void FindCarrierCut_ReturnsEndOfSilentGap()
        {
            // Loud 0.5 s, silent 0.1 s, loud 0.5 s. Search across the whole middle area.
            var pcm = Pcm16(
                ((short)20000, SampleRate / 2),
                ((short)0, SampleRate / 10),
                ((short)20000, SampleRate / 2));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.3, 0.8);

            Assert.InRange(cut, 0.575, 0.6); // late end of the silent gap, not its centre
        }

        [Fact]
        public void FindCarrierCut_WindowOpensInsideCarrierTail_CutsPastTheTail()
        {
            // Whisper's word-end lands early: 0.05 s of carrier tail still sounds after 0.30 s,
            // then the real gap, then the target. The cut must clear the tail.
            var pcm = Pcm16(
                ((short)20000, (int)(SampleRate * 0.35)), // carrier, tail runs to 0.35 s
                ((short)0, (int)(SampleRate * 0.10)),     // gap 0.35 - 0.45 s
                ((short)20000, (int)(SampleRate * 0.35))); // target from 0.45 s
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.30, 0.45);

            Assert.InRange(cut, 0.35, 0.45); // after the carrier tail, before the target
        }

        [Fact]
        public void FindCarrierCut_NoisyGap_IsStillFound()
        {
            // Gap is a low noise floor rather than digital silence — the quiet band is relative.
            var pcm = Pcm16(
                ((short)20000, SampleRate / 2),
                ((short)40, SampleRate / 10),
                ((short)20000, SampleRate / 2));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.3, 0.8);

            Assert.InRange(cut, 0.575, 0.6);
        }

        [Fact]
        public void FindCarrierCut_TwoGaps_PrefersTheLongerOne()
        {
            // Short gap at 0.20 - 0.23 s, longer gap at 0.40 - 0.50 s.
            var pcm = Pcm16(
                ((short)20000, (int)(SampleRate * 0.20)),
                ((short)0, (int)(SampleRate * 0.03)),
                ((short)20000, (int)(SampleRate * 0.17)),
                ((short)0, (int)(SampleRate * 0.10)),
                ((short)20000, (int)(SampleRate * 0.20)));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.15, 0.55);

            Assert.InRange(cut, 0.475, 0.50);
        }

        [Fact]
        public void FindCarrierCut_WindowShorterThan20Ms_ReturnsLateFallback()
        {
            var pcm = Pcm16(((short)20000, SampleRate));
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.400, 0.410);

            Assert.Equal(0.400, cut, precision: 6); // end less the guard, clamped to the window start
        }

        [Fact]
        public void FindCarrierCut_Non16Bit_ReturnsLateFallback()
        {
            // 8-bit PCM: RMS scan unsupported, late fallback.
            var pcm = new byte[SampleRate]; // 1 second of 8-bit mono
            var wav = BuildWav(pcm, channels: 1, bitsPerSample: 8);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.2, 0.6);

            Assert.Equal(0.59, cut, precision: 6);
        }

        [Fact]
        public void FindCarrierCut_ClampsWindowToDuration()
        {
            var pcm = Pcm16(((short)20000, SampleRate / 2)); // 0.5 s total
            var wav = BuildWav(pcm);

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.4, 2.0);

            Assert.InRange(cut, 0.4, 0.5);
        }

        [Fact]
        public void FindCarrierCut_Stereo_FindsSilentGap()
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

            double cut = WavTrimmer.FindCarrierCut(new MemoryStream(wav), 0.3, 0.8);

            Assert.InRange(cut, 0.575, 0.6);
        }
    }
}
