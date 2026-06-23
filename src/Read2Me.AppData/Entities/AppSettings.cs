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
        /// Whether sentence chunking is applied before TTS synthesis. Defaults to true.
        /// </summary>
        public bool SentenceSplitEnabled { get; set; } = true;

        /// <summary>
        /// Silence inserted between adjacent sentences when stitching chunked audio, in
        /// milliseconds. Defaults to 300.
        /// </summary>
        public int SentencePauseMs { get; set; } = 300;

        /// <summary>
        /// Sentence fragments shorter than this are merged into a neighbour rather than
        /// emitted as their own chunk. Defaults to 15. Not surfaced in the UI.
        /// </summary>
        public int SentenceMinChunkChars { get; set; } = 15;

        public int VolumePauseMs { get; set; } = 4000;
        public int PartPauseMs { get; set; } = 3000;
        public int ChapterPauseMs { get; set; } = 2500;
        public int ParagraphPauseMs { get; set; } = 800;
        public int PauseMs { get; set; } = 500;
    }
}
