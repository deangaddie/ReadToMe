using System.Globalization;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure translation of <see cref="SilenceTrimSettings"/> into an ffmpeg filtergraph string:
    /// trim the head, reverse, trim the (now leading) tail, reverse back. No I/O — see
    /// <see cref="SilenceTrimStep"/> for the runner.
    /// </summary>
    public static class SilenceTrimChainBuilder
    {
        public static string Build(SilenceTrimSettings? settings)
        {
            settings ??= new SilenceTrimSettings();

            var trim = BuildSilenceRemove(settings);
            // stop_periods=-1 would strip *mid*-clip silence too, chewing the pauses inside a
            // sentence. Trailing-only trim therefore needs the areverse sandwich.
            return string.Join(", ", [trim, "areverse", trim, "areverse"]);
        }

        private static string BuildSilenceRemove(SilenceTrimSettings settings)
        {
            var filter = "silenceremove=start_periods=1:start_duration=0";

            // start_silence *keeps* that much silence rather than stripping all of it, so the pad
            // is free — no apad/adelay. At 0 the arg is omitted, giving a hard trim.
            if (settings.PadMs > 0)
                filter += $":start_silence={F(settings.PadMs / 1000.0)}";

            return filter + $":start_threshold={F(settings.ThresholdDb)}dB:detection=peak";
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
