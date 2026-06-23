using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WavStitcherTests
    {
        // --- Synthetic WAV builder: 16-bit PCM, configurable sample rate / channels / extra chunks. ---
        private const int SampleRate = 16000;
        private const short Channels = 1;
        private const short BitsPerSample = 16;

        private static byte[] BuildWav(int sampleCount, byte fill = 0x11, bool extraChunkBeforeData = false)
        {
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            int dataLen = sampleCount * blockAlign;
            var pcm = new byte[dataLen];
            Array.Fill(pcm, fill);
            return BuildWavFromPcm(pcm, extraChunkBeforeData);
        }

        private static byte[] BuildWavFromPcm(byte[] pcm, bool extraChunkBeforeData = false)
        {
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            int byteRate = SampleRate * blockAlign;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            byte[] extra = extraChunkBeforeData ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>();
            int extraChunkTotal = extraChunkBeforeData ? 8 + extra.Length : 0;

            int riffSize = 4 + (8 + 16) + extraChunkTotal + (8 + pcm.Length);

            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(riffSize);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write(Channels);
            w.Write(SampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(BitsPerSample);

            if (extraChunkBeforeData)
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("LIST"));
                w.Write(extra.Length);
                w.Write(extra);
            }

            // data chunk
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);

            w.Flush();
            return ms.ToArray();
        }

        private static int ReadDataLength(byte[] wav)
        {
            // Walk chunks after the 12-byte RIFF/WAVE header to find "data".
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "data")
                    return size;
                pos += 8 + size + (size & 1);
            }
            throw new InvalidOperationException("no data chunk");
        }

        private static byte[] Stitch(IEnumerable<byte[]> wavs, int pauseMs)
        {
            var streams = new List<Stream>();
            foreach (var w in wavs)
                streams.Add(new MemoryStream(w));
            using var outStream = WavStitcher.Stitch(streams, pauseMs);
            using var ms = new MemoryStream();
            outStream.CopyTo(ms);
            return ms.ToArray();
        }

        [Fact]
        public void SingleChunk_ReturnsByteIdenticalPassthrough()
        {
            var wav = BuildWav(sampleCount: 100);

            var result = Stitch(new[] { wav }, pauseMs: 300);

            Assert.Equal(wav, result);
        }

        [Fact]
        public void NChunks_DataLength_IsSumOfPcmPlusNMinus1Silences()
        {
            const int pauseMs = 250;
            var a = BuildWav(sampleCount: 100, fill: 0x11);
            var b = BuildWav(sampleCount: 200, fill: 0x22);
            var c = BuildWav(sampleCount: 50, fill: 0x33);

            int blockAlign = Channels * BitsPerSample / 8;
            int pcmPerChunk = (100 + 200 + 50) * blockAlign;
            // Silence bytes for the pause, aligned to a whole sample.
            int silenceSamples = (int)((long)SampleRate * pauseMs / 1000);
            int silenceBytes = silenceSamples * blockAlign;

            var result = Stitch(new[] { a, b, c }, pauseMs);

            int expected = pcmPerChunk + 2 * silenceBytes;
            Assert.Equal(expected, ReadDataLength(result));
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

        [Fact]
        public void Silence_IsBetweenChunksOnly_NoneAtEnds()
        {
            const int pauseMs = 100;
            var a = BuildWav(sampleCount: 20, fill: 0xAA);
            var b = BuildWav(sampleCount: 20, fill: 0xBB);

            var result = ReadData(Stitch(new[] { a, b }, pauseMs));

            int blockAlign = Channels * BitsPerSample / 8;
            int chunkBytes = 20 * blockAlign;
            int silenceBytes = (int)((long)SampleRate * pauseMs / 1000) * blockAlign;

            // First chunk intact at the front, no leading silence.
            for (int i = 0; i < chunkBytes; i++)
                Assert.Equal(0xAA, result[i]);
            // Silence sits between the two chunks.
            for (int i = chunkBytes; i < chunkBytes + silenceBytes; i++)
                Assert.Equal(0x00, result[i]);
            // Second chunk intact at the back, no trailing silence.
            for (int i = chunkBytes + silenceBytes; i < result.Length; i++)
                Assert.Equal(0xBB, result[i]);
            Assert.Equal(chunkBytes + silenceBytes + chunkBytes, result.Length);
        }

        [Fact]
        public void DataChunk_NotAtFixed44Offset_IsParsed()
        {
            // Extra LIST chunk before data pushes it past the usual 44-byte header position.
            var a = BuildWav(sampleCount: 30, fill: 0xAA, extraChunkBeforeData: true);
            var b = BuildWav(sampleCount: 30, fill: 0xBB, extraChunkBeforeData: true);

            var result = ReadData(Stitch(new[] { a, b }, pauseMs: 0));

            int blockAlign = Channels * BitsPerSample / 8;
            Assert.Equal(60 * blockAlign, result.Length); // both chunks, zero pause
            Assert.Equal(0xAA, result[0]);
            Assert.Equal(0xBB, result[^1]);
        }

        [Fact]
        public void Output_HasValidHeaderMatchingConcatenatedData()
        {
            var a = BuildWav(sampleCount: 100);
            var b = BuildWav(sampleCount: 100);

            var result = Stitch(new[] { a, b }, pauseMs: 100);

            // RIFF size must equal total file length minus the 8-byte RIFF id+size prefix.
            int riffSize = BitConverter.ToInt32(result, 4);
            Assert.Equal(result.Length - 8, riffSize);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(result, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(result, 8, 4));
        }
    }
}
