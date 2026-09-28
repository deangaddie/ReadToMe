using System.Text;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WavHeaderTests
    {
        [Fact]
        public void DurationMs_OfACanonicalWav_IsItsLength()
        {
            using var wav = new MemoryStream(TestWav.Tone(18_250));

            Assert.Equal(18_250, WavHeader.TryReadDurationMs(wav)!.Value, precision: 0);
        }

        [Fact]
        public void DurationMs_UsesTheFmtByteRate_AndSkipsChunksBeforeData()
        {
            // 44.1 kHz stereo 16-bit (byte rate 176,400) with a LIST chunk ahead of data, as ffmpeg
            // writes when metadata is not stripped: no fixed 44-byte header to lean on.
            using var wav = new MemoryStream(Wav(byteRate: 176_400, pcmBytes: 176_400 * 2, listChunk: true));

            Assert.Equal(2_000, WavHeader.TryReadDurationMs(wav)!.Value, precision: 0);
        }

        [Fact]
        public void DurationMs_OfSomethingThatIsNotAWav_IsNull()
        {
            using var mp3 = new MemoryStream([0x49, 0x44, 0x33, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

            Assert.Null(WavHeader.TryReadDurationMs(mp3));
        }

        [Fact]
        public void DurationMs_OfATruncatedHeader_IsNull()
        {
            using var wav = new MemoryStream(TestWav.Tone(1_000)[..30]);

            Assert.Null(WavHeader.TryReadDurationMs(wav));
        }

        [Fact]
        public void DurationMs_OfAStoredProjectFile_ReadsItsHeader_AndIsNullWhenAbsent()
        {
            var fs = new FakeFileSystem();
            var folder = new ProjectFolderId("P");
            fs.SeedFile(Path.Combine("C:\\fake-workspace", "P", "voices", "c", "v.wav"), TestWav.Tone(16_000));

            Assert.Equal(16_000, WavHeader.TryReadDurationMs(fs, folder, "voices/c/v.wav")!.Value, precision: 0);
            Assert.Null(WavHeader.TryReadDurationMs(fs, folder, "voices/c/missing.wav"));
            Assert.Null(WavHeader.TryReadDurationMs(fs, folder, null));
        }

        private static byte[] Wav(int byteRate, int pcmBytes, bool listChunk)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var list = listChunk ? "INFOISFT\x0e\0\0\0Lavf61.7.100\0\0"u8.ToArray() : [];

            bw.Write("RIFF"u8.ToArray());
            bw.Write(0); // a streamed RIFF size is often left unset; the reader does not rely on it
            bw.Write("WAVE"u8.ToArray());
            bw.Write("fmt "u8.ToArray());
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)2);
            bw.Write(44_100);
            bw.Write(byteRate);
            bw.Write((short)4);
            bw.Write((short)16);
            if (listChunk)
            {
                bw.Write(Encoding.ASCII.GetBytes("LIST"));
                bw.Write(list.Length);
                bw.Write(list);
            }
            bw.Write("data"u8.ToArray());
            bw.Write(pcmBytes);
            bw.Write(new byte[pcmBytes]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
