namespace Read2Me.Data.Enums
{
    /// <summary>
    /// One row of the Overview's stepper, in the order a project walks them. Serialised camelCase on
    /// the wire, where the same strings are the web's step ids and the checkpoint route segment.
    /// </summary>
    public enum PipelineStep
    {
        Import,
        Structure,
        Speakers,
        Text,
        Voices,
        Titles,
        Pauses,
        Audio,
        Export,
    }
}
