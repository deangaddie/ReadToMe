namespace Read2Me.AppData.Entities
{
    public class AppSettings
    {
        public int Id { get; set; }
        public int? SelectedThemeId { get; set; }
        public int? ActiveLlmConfigId { get; set; }
        public int? ActiveTranscriptionConfigId { get; set; }
        public int? ActiveVoiceDesignConfigId { get; set; }
        public int? ActiveParagraphTtsConfigId { get; set; }
        public int? ActiveSemanticConfigId { get; set; }
        public bool FollowSystemPreference { get; set; }

        /// <summary>
        /// Sample text sent to the voice-design service for every voice generation.
        /// Null means the built-in default is used.
        /// </summary>
        public string? VoiceDesignSampleText { get; set; }

        /// <summary>
        /// Path to the ffmpeg executable used by the audio post-processing pipeline.
        /// Null/blank means rely on PATH.
        /// </summary>
        public string? FfmpegPath { get; set; }

        /// <summary>
        /// Word-error-rate pass threshold for transcript verification. Defaults to 0.15.
        /// </summary>
        public double WerThreshold { get; set; } = 0.15;

        /// <summary>
        /// Legacy sentence-split path. Off by default; hidden from UI. Kept until chunking is proven.
        /// </summary>
        public bool SentenceSplitEnabled { get; set; } = false;

        /// <summary>
        /// Silence inserted between stitched chunks, in milliseconds. Defaults to 300.
        /// </summary>
        public int ChunkPauseMs { get; set; } = 300;

        public int VolumePauseMs { get; set; } = 4000;
        public int PartPauseMs { get; set; } = 3000;
        public int ChapterPauseMs { get; set; } = 2500;
        public int ParagraphPauseMs { get; set; } = 800;
        public int PauseMs { get; set; } = 500;
    }
}
