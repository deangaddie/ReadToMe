using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public sealed class SemanticSimilarityClientResolver(IServiceProvider services)
        : ISemanticSimilarityClientResolver
    {
        public ISemanticSimilarityClient Resolve(SemanticSimilarityServiceType type)
        {
            var client = services.GetKeyedService<ISemanticSimilarityClient>(type);
            if (client is null)
                throw new NotSupportedException(
                    $"No semantic similarity client registered for type '{type}'.");
            return client;
        }
    }
}
