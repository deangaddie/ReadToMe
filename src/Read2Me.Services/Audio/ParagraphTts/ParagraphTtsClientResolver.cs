using System;
using Microsoft.Extensions.DependencyInjection;
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
            return client;
        }
    }
}
