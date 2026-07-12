namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Settings payload for the silence-trim post-process step. Raw params, not a preset
    /// reference: one obvious behaviour, two fields the user can read straight off the form.
    /// </summary>
    /// <param name="ThresholdDb">Peak level below which a sample counts as silence.</param>
    /// <param name="PadMs">Silence deliberately kept at each end. 0 = hard trim.</param>
    public sealed record SilenceTrimSettings(double ThresholdDb = -50, int PadMs = 50);
}
