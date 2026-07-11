namespace Read2Me.AppData.Entities
{
    public class LlmPromptSettings
    {
        public int Id { get; set; }

        /// <summary>Book character-attribution prompt template. Null => use built-in default.</summary>
        public string? CharacterPrompt { get; set; }

        /// <summary>Batch (multi-paragraph) character-attribution prompt template. Null => use built-in default.</summary>
        public string? BatchCharacterPrompt { get; set; }

        /// <summary>Strict (explicit-tag-only) character-attribution prompt template for small models. Null => use built-in default.</summary>
        public string? SimpleCharacterPrompt { get; set; }

        /// <summary>Strict (explicit-tag-only) batch character-attribution prompt template for small models. Null => use built-in default.</summary>
        public string? SimpleBatchCharacterPrompt { get; set; }

        /// <summary>Character voice-design prompt template. Null => use built-in default.</summary>
        public string? VoicePrompt { get; set; }

        /// <summary>Character voice-plan prompt template (JSON list of all voices a character needs). Null => use built-in default.</summary>
        public string? VoicePlanPrompt { get; set; }

        /// <summary>Narrator voice-plan prompt template. Asks for one steady narration voice, never per-character voices. Null => use built-in default.</summary>
        public string? NarratorVoicePlanPrompt { get; set; }

        /// <summary>Character-discovery prompt template (JSON list of the book's notable characters and aliases). Null => use built-in default.</summary>
        public string? DiscoverCharactersPrompt { get; set; }

        /// <summary>Preceding paragraphs sent as LLM context. Null => use built-in default.</summary>
        public int? ContextParagraphsBefore { get; set; }

        /// <summary>Following paragraphs sent as LLM context. Null => use built-in default.</summary>
        public int? ContextParagraphsAfter { get; set; }
    }
}
