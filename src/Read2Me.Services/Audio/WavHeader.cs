using System.Buffers.Binary;
using System.Text;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Reads a WAV's duration from its header alone: the <c>fmt </c> byte rate and the <c>data</c>
    /// chunk size, walking the RIFF chunks between them (ffmpeg may write a <c>LIST</c> chunk first,
    /// so there is no fixed 44-byte header). The PCM itself is never read.
    /// </summary>
    public static class WavHeader
    {
        /// <summary>
        /// Duration in milliseconds, or null when the stream is not a PCM WAV whose header can be read.
        /// A seekable stream skips chunk bodies; a data size left unset by a streaming writer
        /// (0 or 0xFFFFFFFF) falls back to the bytes that follow the chunk header.
        /// </summary>
        public static double? TryReadDurationMs(Stream wav)
        {
            Span<byte> header = stackalloc byte[12];
            if (!TryRead(wav, header) ||
                Encoding.ASCII.GetString(header[..4]) != "RIFF" ||
                Encoding.ASCII.GetString(header[8..12]) != "WAVE")
                return null;

            int? byteRate = null;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (TryRead(wav, chunk))
            {
                var id = Encoding.ASCII.GetString(chunk[..4]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);

                if (id == "fmt ")
                {
                    if (size < 16 || !TryRead(wav, fmt)) return null;
                    byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[8..12]);
                    if (!Skip(wav, size - 16 + (size & 1))) return null;
                }
                else if (id == "data")
                {
                    if (byteRate is not > 0) return null;
                    long dataBytes = size is 0 or uint.MaxValue && wav.CanSeek
                        ? wav.Length - wav.Position
                        : size;
                    return dataBytes * 1000.0 / byteRate.Value;
                }
                else if (!Skip(wav, size + (size & 1)))
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// The duration of a project-relative WAV (a Voice's <c>AudioFileName</c>), or null when there
        /// is none, it cannot be opened, or its header cannot be read. Never throws: a listing must not
        /// fail over one unreadable file.
        /// </summary>
        public static double? TryReadDurationMs(IFileSystem fs, ProjectFolderId folder, string? relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                var path = fs.ProjectFilePath(folder, relativePath);
                if (!fs.FileExists(path)) return null;
                using var stream = fs.OpenRead(path);
                return TryReadDurationMs(stream);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool TryRead(Stream s, Span<byte> buffer)
        {
            try
            {
                s.ReadExactly(buffer);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        private static bool Skip(Stream s, long count)
        {
            if (s.CanSeek)
            {
                if (s.Position + count > s.Length) return false;
                s.Seek(count, SeekOrigin.Current);
                return true;
            }

            var buffer = new byte[Math.Min(count, 4096)];
            while (count > 0)
            {
                var read = s.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (read == 0) return false;
                count -= read;
            }
            return true;
        }
    }
}
