using System;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.Transcription
{
    /// <summary>
    /// Resolves the <see cref="ITranscriptionClient"/> registered (keyed by
    /// <see cref="TranscriptionServiceType"/>) in DI.
    /// </summary>
    public sealed class TranscriptionClientResolver(IServiceProvider services)
        : ITranscriptionClientResolver
    {
        public ITranscriptionClient Resolve(TranscriptionServiceType type)
        {
            var client = services.GetKeyedService<ITranscriptionClient>(type);
            if (client is null)
                throw new NotSupportedException(
                    $"No transcription client registered for type '{type}'.");
            return client;
        }
    }
}
