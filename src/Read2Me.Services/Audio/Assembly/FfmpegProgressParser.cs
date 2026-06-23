using System;
using System.Text.RegularExpressions;

namespace Read2Me.Services.Audio.Assembly
{
    /// <summary>
    /// Parses ffmpeg stderr  time=HH:MM:SS.xx  lines and converts them to a 0..1 progress
    /// fraction relative to a known total duration.
    /// Pure / static — no I/O, no CliWrap dependency.
    /// </summary>
    public static class FfmpegProgressParser
    {
        // Matches "time=HH:MM:SS.xx" (fractional seconds, 2 decimal places typical but flexible)
        private static readonly Regex TimePattern =
            new(@"time=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

        /// <summary>
        /// Parses the last  time=HH:MM:SS.xx  occurrence in <paramref name="stderrLine"/> and
        /// returns elapsed / total clamped to [0, 1].  Returns null when no match.
        /// </summary>
        public static double? ParseProgress(string stderrLine, TimeSpan totalDuration)
        {
            if (totalDuration <= TimeSpan.Zero)
                return null;

            var match = TimePattern.Match(stderrLine);
            if (!match.Success)
                return null;

            // Walk all matches to get the last one (ffmpeg may print multiple per line)
            var last = match;
            while (match.Success)
            {
                last = match;
                match = match.NextMatch();
            }

            if (!int.TryParse(last.Groups[1].Value, out var hours) ||
                !int.TryParse(last.Groups[2].Value, out var minutes) ||
                !double.TryParse(last.Groups[3].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds))
                return null;

            var elapsed = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
            var fraction = elapsed.TotalSeconds / totalDuration.TotalSeconds;
            return Math.Clamp(fraction, 0.0, 1.0);
        }
    }
}
