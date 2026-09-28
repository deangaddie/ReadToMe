namespace Read2Me.AppData.Entities
{
    /// <summary>Selects the paragraph-TTS backend and its settings shape.</summary>
    public enum ParagraphTtsServiceType
    {
        VoxCpm2 = 0,
        Chatterbox = 1,
        // ChatterboxTurbo = 2,  // reserved, never reuse — removed by ADR 0010 (see ParagraphTtsServiceTypes)
        Qwen3Base = 3,
        /// <summary>Breeze TTS 2 voice cloning on the audio.cpp runtime; follows per-item VoiceInstructions.</summary>
        Breeze = 4,
    }

    public static class ParagraphTtsServiceTypes
    {
        /// <summary>
        /// Chatterbox Turbo's value, removed by ADR 0010: audio.cpp cannot clone with it and nothing
        /// wrote its paralinguistic tags. Reserved so it is never reused; rows still carrying it
        /// (until the audiocpp-tts 09 migration deletes them) fail when resolved rather than crash.
        /// </summary>
        public const ParagraphTtsServiceType RemovedChatterboxTurbo = (ParagraphTtsServiceType)2;
    }
}
