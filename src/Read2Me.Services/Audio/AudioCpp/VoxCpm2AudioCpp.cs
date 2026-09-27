using System.Globalization;

namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// VoxCPM2's audio.cpp request conventions, shared by paragraph TTS and voice design (one
    /// <c>voxcpm2</c> model entry, task <c>tts</c>, serves clone, control and design).
    /// </summary>
    public static class VoxCpm2AudioCpp
    {
        /// <summary>
        /// VoxCPM2 takes its control — a voice instruction or a design prompt — inside the text, as
        /// <c>(control)text</c>, exactly as the native server prepended it. Blank control sends the text as is.
        /// </summary>
        public static string Input(string text, string? control) =>
            string.IsNullOrWhiteSpace(control) ? text : $"({control.Trim()}){text}";

        /// <summary>The native server's knobs under audio.cpp's option names, as invariant strings.</summary>
        public static Dictionary<string, string> Options(
            double cfgValue, int inferenceTimesteps, int minLen, int maxLen,
            bool retryBadcase, int retryBadcaseMaxTimes, double retryBadcaseRatioThreshold, int? seed) => new()
        {
            ["guidance_scale"] = Inv(cfgValue),
            ["num_inference_steps"] = Inv(inferenceTimesteps),
            ["min_tokens"] = Inv(minLen),
            ["max_tokens"] = Inv(maxLen),
            ["retry_badcase"] = retryBadcase ? "true" : "false",
            ["retry_badcase_max_times"] = Inv(retryBadcaseMaxTimes),
            ["retry_badcase_ratio_threshold"] = Inv(retryBadcaseRatioThreshold),
            ["seed"] = AudioCppSeed.For(seed),
        };

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
