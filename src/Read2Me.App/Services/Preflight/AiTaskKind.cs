namespace Read2Me.App.Services.Preflight
{
    /// <summary>
    /// The user-initiated tasks that need Docker AI services up before they can run. Each kind maps
    /// to the active configs whose endpoints the task will call; batch character operations reuse
    /// the single-item kinds (same services, same endpoints).
    /// </summary>
    public enum AiTaskKind
    {
        /// <summary>Dialog attribution via the active LLM config.</summary>
        CharacterAttribution,

        /// <summary>Paragraph audio pipeline: active TTS + transcription + semantic-similarity configs.</summary>
        AudioGeneration,

        /// <summary>Voice design prompt generation via the active LLM config (single or batch).</summary>
        VoicePromptGeneration,

        /// <summary>Voice audio synthesis via the active voice-design config (single or batch).</summary>
        VoiceDesignAudio,

        /// <summary>Reference-audio transcription via the active transcription config.</summary>
        Transcription,
    }
}
