using System;
using System.Buffers.Binary;
using System.IO;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>Writes float32-LE PCM samples as a 16-bit PCM WAV byte buffer.</summary>
    public static class WavWriter
    {
        /// <summary>
        /// Converts float32 little-endian PCM (range [-1,1]) to a complete 16-bit PCM
        /// WAV file in a rewound MemoryStream.
        /// </summary>
        public static MemoryStream WriteInt16Pcm(
            ReadOnlySpan<byte> float32LeSamples, int sampleRate, int channels = 1)
        {
            int floatCount = float32LeSamples.Length / 4;
            int dataBytes = floatCount * 2; // int16 per sample
            short bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);

            var ms = new MemoryStream(44 + dataBytes);
            var w = new BinaryWriter(ms);

            // RIFF header
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);          // chunk size
            w.Write("WAVE"u8.ToArray());
            // fmt chunk
            w.Write("fmt "u8.ToArray());
            w.Write(16);                      // PCM fmt chunk size
            w.Write((short)1);                // audio format = PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bitsPerSample);
            // data chunk
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);

            for (int i = 0; i < floatCount; i++)
            {
                float f = BinaryPrimitives.ReadSingleLittleEndian(
                    float32LeSamples.Slice(i * 4, 4));
                f = Math.Clamp(f, -1f, 1f);
                short s = (short)Math.Round(f * 32767f);
                w.Write(s);
            }

            w.Flush();
            ms.Position = 0;
            return ms;
        }
    }
}
