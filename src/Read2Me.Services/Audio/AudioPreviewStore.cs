using System.Collections.Concurrent;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Holds the rendered A/B preview WAV for each browser circuit, keyed by a caller-supplied
    /// token. One file per token, overwritten on every render — previews are throwaway, so there
    /// is no persistence and no cleanup job (the OS temp dir is the cleanup job).
    /// </summary>
    public sealed class AudioPreviewStore
    {
        private readonly ConcurrentDictionary<string, string> _paths = new();
        private readonly string _directory;

        public AudioPreviewStore(string? directory = null)
        {
            _directory = directory ?? Path.Combine(Path.GetTempPath(), "read2me-preview");
        }

        public async Task SaveAsync(string token, byte[] wav, CancellationToken ct = default)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{token}.wav");
            await File.WriteAllBytesAsync(path, wav, ct);
            _paths[token] = path;
        }

        /// <summary>The stored preview for <paramref name="token"/>, or false when nothing is rendered.</summary>
        public bool TryGetPath(string token, out string? path)
        {
            if (_paths.TryGetValue(token, out var stored) && File.Exists(stored))
            {
                path = stored;
                return true;
            }

            path = null;
            return false;
        }
    }
}
