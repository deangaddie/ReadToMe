namespace Read2Me.AppData.Entities
{
    public class SemanticSimilarityServiceConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public SemanticSimilarityServiceType Type { get; set; }
        public string SettingsJson { get; set; } = string.Empty;
    }
}
