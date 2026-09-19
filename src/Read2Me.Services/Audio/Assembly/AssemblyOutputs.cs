using System.Diagnostics.CodeAnalysis;

namespace Read2Me.Services.Audio.Assembly
{
    /// <summary>A finished audiobook under a project's <c>output/</c> folder.</summary>
    public sealed record AssemblyOutput(string FileName, long SizeBytes, DateTimeOffset CreatedAt, bool IsPartial);

    /// <summary>
    /// Where assembled audiobooks live and what they are called: <c>{project}/output/{title}.m4b</c>,
    /// or <c>{title}_partial_{yyyyMMdd}.m4b</c> for a build that skipped items without audio. The
    /// assembly service writes by this convention and the output endpoints read by it.
    /// </summary>
    public static class AssemblyOutputs
    {
        public const string Extension = ".m4b";
        private const string PartialMarker = "_partial_";

        public static string DirectoryOf(string projectFolderPath) => Path.Combine(projectFolderPath, "output");

        public static string FileName(string bookTitle, bool partial, DateTime date) =>
            Sanitize(bookTitle) + (partial ? $"{PartialMarker}{date:yyyyMMdd}" : string.Empty) + Extension;

        public static bool IsPartial(string fileName) =>
            fileName.Contains(PartialMarker, StringComparison.OrdinalIgnoreCase);

        /// <summary>The project's finished audiobooks, newest first. In-flight <c>.tmp</c> encodes are not outputs.</summary>
        public static IReadOnlyList<AssemblyOutput> List(string projectFolderPath)
        {
            var dir = new DirectoryInfo(DirectoryOf(projectFolderPath));
            if (!dir.Exists) return [];

            return dir.EnumerateFiles("*" + Extension)
                // The 8.3 short-name match on Windows lets "*.m4b" catch longer extensions.
                .Where(f => f.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new AssemblyOutput(
                    f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), IsPartial(f.Name)))
                .ToList();
        }

        /// <summary>
        /// Resolves a client-supplied output name to its file. The name must be a bare <c>.m4b</c>
        /// file name — a separator or a parent reference never reaches a path — and must exist.
        /// </summary>
        public static bool TryResolve(string projectFolderPath, string? fileName, [NotNullWhen(true)] out string? path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOfAny(['/', '\\']) >= 0 ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                return false;

            var candidate = Path.Combine(DirectoryOf(projectFolderPath), fileName);
            if (!File.Exists(candidate)) return false;

            path = candidate;
            return true;
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        }
    }
}
