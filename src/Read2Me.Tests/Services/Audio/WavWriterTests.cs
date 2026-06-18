using System;
using System.Buffers.Binary;
using Read2Me.Services.Audio.VoiceDesign;
using Xunit;

namespace Read2Me.Tests.Services.Audio;

public class WavWriterTests
{
    [Fact]
    public void WriteInt16Pcm_ProducesValidWavHeaderAndSampleCount()
    {
        // two float samples: +1.0 and -1.0
        var floats = new float[] { 1.0f, -1.0f };
        var bytes = new byte[floats.Length * 4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), floats[0]);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), floats[1]);

        using var wav = WavWriter.WriteInt16Pcm(bytes, 48000, 1);
        var arr = wav.ToArray();

        Assert.Equal((byte)'R', arr[0]);                 // RIFF
        Assert.Equal(44 + 4, arr.Length);                // header + 2*int16
        short s0 = BitConverter.ToInt16(arr, 44);
        short s1 = BitConverter.ToInt16(arr, 46);
        Assert.Equal(32767, s0);                         // +1.0 -> max
        Assert.Equal(-32767, s1);                        // -1.0 -> ~min
    }
}
