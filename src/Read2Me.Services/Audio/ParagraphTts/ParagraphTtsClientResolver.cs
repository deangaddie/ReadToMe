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

            // Carrier prefix sits below the chunker so short tail chunks of long paragraphs
            // also get carrier treatment; each chunk trims independently before stitching.
            var carrierLogger = services.GetRequiredService<ILogger<CarrierPrefixTtsClient>>();
            var carrier = new CarrierPrefixTtsClient(
                client,
                services.GetRequiredService<Transcription.ITranscriptionClientResolver>(),
                services.GetRequiredService<TranscriptionSettingsService>(),
                carrierLogger);

            var settings = services.GetRequiredService<AudioProcessingSettingsService>();
            var chunkLogger = services.GetRequiredService<ILogger<SentenceChunkedTtsClient>>();
            var chunked = new SentenceChunkedTtsClient(carrier, settings, chunkLogger);

            // Preprocessing wraps chunking so escape/prosody steps run on whole-paragraph text
            // before chunk boundaries are computed.
            var prepLogger = services.GetRequiredService<ILogger<TextPreprocessingTtsClient>>();
            return new TextPreprocessingTtsClient(chunked, services, prepLogger);
        }
    }
}
