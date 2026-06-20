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
    }
}
