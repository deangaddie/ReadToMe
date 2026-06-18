namespace Read2Me.AppData.Entities
{
    /// <summary>Selects the voice-design backend and its settings shape.</summary>
    public enum VoiceDesignServiceType
    {
        VoxCpm2 = 0,
        Qwen3 = 1,
        // Chatterbox = 2,  // reserved — needs reference audio, no prompt-only design mode
    }
}
