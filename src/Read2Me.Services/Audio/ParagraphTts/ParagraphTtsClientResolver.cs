using System;
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

            // Single wrap point: every provider type is wrapped in the self-gating chunking
            // decorator, so chunking applies uniformly and the processor stays unaware of it.
            var settings = services.GetRequiredService<AudioProcessingSettingsService>();
            var logger = services.GetRequiredService<ILogger<SentenceChunkedTtsClient>>();
            return new SentenceChunkedTtsClient(client, settings, logger);
        }
    }
}
