namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the silence-trim post-process step. Raw params, not a preset
    /// reference: one obvious behaviour, three fields the user can read straight off the form.
    /// </summary>
    /// <param name="ThresholdDb">
    /// Peak level below which a sample counts as silence. <c>detection=peak</c> compares against the
    /// noise floor's <i>peak</i>, so the paragraph default of −50 removes nothing at all from a mic
    /// recording — the Voice scope defaults to −35 (see <see cref="AudioPostProcessStepDefaults"/>).
    /// </param>
    /// <param name="PadMs">Silence deliberately kept at each end. 0 = hard trim.</param>
    /// <param name="MinOutputMs">
    /// Output shorter than this is treated as a failed trim. An absolute floor, not a percentage: a
    /// legitimate trim can remove 80%+ of a short paragraph item (a one-word "Yes." after two seconds
    /// of dead air), so a percentage rule would falsely skip exactly the case this step exists for.
    /// A property of <c>(step, scope)</c> rather than of the step — a reference voice that trims down
    /// to under a second has gone wrong, but a paragraph item that does has not.
    /// </param>
    public sealed record SilenceTrimSettings(double ThresholdDb = -50, int PadMs = 50, double MinOutputMs = 200)
    {
        /// <summary>
        /// The voice editor's dial range for <see cref="ThresholdDb"/>. −30 dB is the line where
        /// speech starts going, so the voice-side dial stops at −35. Shared by the Blazor dials and
        /// the API's step catalog so the two editors offer (and the API enforces) the same range.
        /// </summary>
        public const double VoiceMinThresholdDb = -60;
        public const double VoiceMaxThresholdDb = -35;
        public const double VoiceThresholdStepDb = 1;

        public const int VoiceMinPadMs = 0;
        public const int VoiceMaxPadMs = 500;
        public const int VoicePadStepMs = 10;
    }
}
