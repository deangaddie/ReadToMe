namespace Read2Me.Services.Audio.SemanticSimilarity.Settings
{
    public sealed record SemanticSimilaritySettings
    {
        public string BaseUrl { get; init; } = string.Empty;
        public double PassThreshold { get; init; } = 0.85;
    }
}
