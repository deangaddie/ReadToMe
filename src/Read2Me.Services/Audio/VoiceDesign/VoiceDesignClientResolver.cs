using System;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public sealed class VoiceDesignClientResolver(IServiceProvider services)
        : IVoiceDesignClientResolver
    {
        public IVoiceDesignClient Resolve(VoiceDesignServiceType type)
        {
            var client = services.GetKeyedService<IVoiceDesignClient>(type);
            if (client is null)
                throw new NotSupportedException(
                    $"No voice-design client registered for type '{type}'.");
            return client;
        }
    }
}
