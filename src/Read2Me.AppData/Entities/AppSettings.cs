namespace Read2Me.AppData.Entities
{
    public class AppSettings
    {
        public int Id { get; set; }
        public int? SelectedThemeId { get; set; }
        public int? ActiveLlmConfigId { get; set; }
        public int? ActiveTranscriptionConfigId { get; set; }
        public int? ActiveVoiceDesignConfigId { get; set; }
    }
}
