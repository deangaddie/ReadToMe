using System.Collections.Generic;
using System.Text;

namespace Read2Me.Services.Audio.Assembly
{
    /// <summary>
    /// Generates the text content of an ffmpeg concat-demuxer list file.
    /// Each line is  file 'path'  with single-quotes escaped per concat-demuxer rules.
    /// Pure / static — no I/O; the caller writes the string to a file.
    /// </summary>
    public static class ConcatListBuilder
    {
        /// <summary>
        /// Produces a concat-list string from an ordered sequence of absolute file paths.
        /// Single-quotes in paths are escaped as  '\''  (end-quote, literal-quote, re-open-quote)
        /// so the concat demuxer parser sees them as unambiguous.
        /// </summary>
        public static string Build(IEnumerable<string> filePaths)
        {
            var sb = new StringBuilder();
            foreach (var path in filePaths)
            {
                sb.Append("file '");
                sb.Append(EscapePath(path));
                sb.AppendLine("'");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes single-quotes in a file path for use inside a concat-demuxer  file '...'  entry.
        /// </summary>
        internal static string EscapePath(string path) =>
            path.Replace("'", @"'\''");
    }
}
