using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Read2Me.Services.Audio.VoiceDesign;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WavWriterTests
    {
        [Fact]
        public void WriteInt16Pcm_ProducesValidRiffHeader_AndConvertsFloatRange()
        {
            // Two samples: 1.0 (max) and -1.0 (min)
            var floatData = new byte[8];
            BitConverter.TryWriteBytes(floatData.AsSpan(0, 4), 1.0f);
            BitConverter.TryWriteBytes(floatData.AsSpan(4, 4), -1.0f);

            using var result = WavWriter.WriteInt16Pcm(floatData, 24000);
            var wav = result.ToArray();

            // Header checks
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
            Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

            // Format checks
            Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
            Assert.Equal(16, BitConverter.ToInt16(wav, 34)); // bits per sample

            // Data length check (2 samples * 2 bytes = 4)
            Assert.Equal(4, BitConverter.ToInt32(wav, 40));

            // Sample conversion checks
            // 1.0 -> 32767
            Assert.Equal(32767, BitConverter.ToInt16(wav, 44));
            // -1.0 -> -32767
            Assert.Equal(-32767, BitConverter.ToInt16(wav, 46));
        }
    }
}
