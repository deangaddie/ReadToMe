namespace Read2Me.AppData.Entities
{
    /// <summary>Selects the paragraph-TTS backend and its settings shape.</summary>
    public enum ParagraphTtsServiceType
    {
        VoxCpm2 = 0,
        Chatterbox = 1,
        // ChatterboxTurbo = 2,  // reserved, never reuse — removed by ADR 0010 (see RetiredParagraphTtsServiceTypes)
        Qwen3Base = 3,
        /// <summary>Breeze TTS 2 voice cloning on the audio.cpp runtime; follows per-item VoiceInstructions.</summary>
        Breeze = 4,
    }

    /// <summary>Paragraph-TTS type values no provider offers any more; reserved so they are never reused.</summary>
    public static class RetiredParagraphTtsServiceTypes
    {
        /// <summary>
        /// Chatterbox Turbo, removed by ADR 0010: audio.cpp cannot clone with it and nothing wrote its
        /// paralinguistic tags. Rows still carrying it (until the audiocpp-tts 09 migration deletes
        /// them) fail with <see cref="ChatterboxTurboRemoved"/> rather than crash.
        /// </summary>
        public const ParagraphTtsServiceType ChatterboxTurbo = (ParagraphTtsServiceType)2;

        public const string ChatterboxTurboRemoved =
            "The Chatterbox Turbo TTS provider was removed; use another paragraph-TTS type or config.";
    }
}
