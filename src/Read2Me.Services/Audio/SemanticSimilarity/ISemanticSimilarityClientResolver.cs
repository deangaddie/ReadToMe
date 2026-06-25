using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public interface ISemanticSimilarityClientResolver
    {
        ISemanticSimilarityClient Resolve(SemanticSimilarityServiceType type);
    }
}
