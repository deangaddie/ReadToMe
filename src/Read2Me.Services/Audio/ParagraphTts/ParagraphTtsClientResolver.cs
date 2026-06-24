using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.ParagraphTts
{
    public sealed class ParagraphTtsClientResolver(IServiceProvider services)
        : IParagraphTtsClientResolver
    {
        public IParagraphTtsClient Resolve(ParagraphTtsServiceType type)
        {
            var client = services.GetKeyedService<IParagraphTtsClient>(type);
            if (client is null)
                throw new NotSupportedException(
                    $"No paragraph-TTS client registered for type '{type}'.");

            var settings = services.GetRequiredService<AudioProcessingSettingsService>();
            var chunkLogger = services.GetRequiredService<ILogger<SentenceChunkedTtsClient>>();
            var chunked = new SentenceChunkedTtsClient(client, settings, chunkLogger);

            // Preprocessing wraps chunking so escape/prosody steps run on whole-paragraph text
            // before chunk boundaries are computed.
            var prepLogger = services.GetRequiredService<ILogger<TextPreprocessingTtsClient>>();
            return new TextPreprocessingTtsClient(chunked, services, prepLogger);
        }
    }
}
