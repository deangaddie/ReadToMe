namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Which audio a post-process step is offered for. Paragraph-item audio is synthetic (it has no
    /// capture artefacts), voice reference audio may be mic-recorded, so the two scopes want
    /// different steps <i>and</i> different defaults for the steps they share — see
    /// <see cref="AudioPostProcessStepDefaults.For"/>.
    /// </summary>
    public enum StepScope
    {
        Paragraph,
        Voice,
    }
}
