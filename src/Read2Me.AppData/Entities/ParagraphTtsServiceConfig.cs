namespace Read2Me.AppData.Entities
{
    /// <summary>
    /// A configured paragraph-TTS backend. Common identity lives in columns; all
    /// backend-specific settings are serialized into <see cref="SettingsJson"/>,
    /// keyed by <see cref="Type"/>. New backend types stay additive.
    /// </summary>
    public class ParagraphTtsServiceConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ParagraphTtsServiceType Type { get; set; }
        public string SettingsJson { get; set; } = string.Empty;
    }
}
