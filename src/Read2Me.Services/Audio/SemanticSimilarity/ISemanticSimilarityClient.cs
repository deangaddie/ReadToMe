using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public interface ISemanticSimilarityClient
    {
        Task<double> ComputeAsync(
            SemanticSimilarityServiceConfig config,
            string text1,
            string text2,
            CancellationToken ct = default);
    }
}
