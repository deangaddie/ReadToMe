namespace Read2Me.AppData.Entities
{
    /// <summary>Selects the paragraph-TTS backend and its settings shape.</summary>
    public enum ParagraphTtsServiceType
    {
        VoxCpm2 = 0,
        Chatterbox = 1,
        ChatterboxTurbo = 2,
        Qwen3Base = 3,
    }
}
