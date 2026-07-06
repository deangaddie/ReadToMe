using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ChatterboxTurboParagraphTtsClientTests
    {
        private static readonly ParagraphTtsServiceConfig Config = new()
        {
            Name = "test",
            Type = ParagraphTtsServiceType.ChatterboxTurbo,
            SettingsJson = """{"baseUrl":"http://test"}""",
        };

        private static byte[] FakeWav() =>
            Encoding.ASCII.GetBytes("RIFF____WAVEfmt ");

        [Fact]
        public async Task GenerateAsync_PostsMultipartToTtsTurboEndpoint_WithTextAndReferenceAudio()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(Encoding.UTF8.GetBytes("fake-wav"));
            await sut.GenerateAsync("well [laugh] hello", "ignored instructions", refAudio, Config, null);

            Assert.Single(handler.Requests);
            var req = handler.Requests[0];
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/tts/turbo", req.RequestUri!.AbsolutePath);
            Assert.IsType<MultipartFormDataContent>(req.Content);

            Assert.Equal("well [laugh] hello", handler.Fields["text"]);
        }

        [Fact]
        public async Task GenerateAsync_ForwardsTemperatureAndRepetitionPenalty_InvariantCultureFormatted()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await sut.GenerateAsync("text", null, refAudio, Config, null);

            Assert.Equal((0.8).ToString(CultureInfo.InvariantCulture), handler.Fields["temperature"]);
            Assert.Equal((1.2).ToString(CultureInfo.InvariantCulture), handler.Fields["repetition_penalty"]);
        }

        [Fact]
        public async Task GenerateAsync_IgnoresVoiceInstructionsAndReferenceTranscript()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await sut.GenerateAsync("text", "some instructions", refAudio, Config, null, referenceTranscript: "some transcript");

            Assert.False(handler.Fields.ContainsKey("instructions"));
            Assert.False(handler.Fields.ContainsKey("voice_transcript"));
        }

        [Fact]
        public async Task GenerateAsync_ReturnsWavStream_PassThrough()
        {
            var wav = FakeWav();
            var handler = new FakeHttpMessageHandler(wav);
            var factory = new FakeHttpClientFactory(handler);
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            var result = await sut.GenerateAsync("text", null, refAudio, Config, null);

            var buf = new byte[wav.Length];
            await result.ReadExactlyAsync(buf);
            Assert.Equal(wav, buf);
        }

        [Fact]
        public async Task GenerateAsync_OnHttpFailure_ManagedService_ThrowsAiServiceUnavailableException()
        {
            var handler = new FakeHttpMessageHandler(FakeWav(), failWith: HttpStatusCode.InternalServerError);
            var factory = new FakeHttpClientFactory(handler);
            var reporter = new FakeAiServiceReporter { Managed = true };
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, reporter);

            using var refAudio = new MemoryStream(new byte[4]);
            await Assert.ThrowsAsync<AiServiceUnavailableException>(() =>
                sut.GenerateAsync("text", null, refAudio, Config, null));

            Assert.Single(reporter.Failures);
        }

        [Fact]
        public async Task GenerateAsync_OnHttpFailure_UnmanagedService_RethrowsOriginal()
        {
            var handler = new FakeHttpMessageHandler(FakeWav(), failWith: HttpStatusCode.InternalServerError);
            var factory = new FakeHttpClientFactory(handler);
            var reporter = new FakeAiServiceReporter { Managed = false };
            var sut = new ChatterboxTurboParagraphTtsClient(factory, NullLogger<ChatterboxTurboParagraphTtsClient>.Instance, reporter);

            using var refAudio = new MemoryStream(new byte[4]);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                sut.GenerateAsync("text", null, refAudio, Config, null));
        }

        private class FakeHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new HttpClient(handler);
        }

        private class FakeHttpMessageHandler(byte[] wavBody, HttpStatusCode? failWith = null) : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = new();
            public Dictionary<string, string> Fields { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);

                if (request.Content is MultipartFormDataContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                        if (name is null) continue;
                        if (part is StreamContent || (part.Headers.ContentDisposition?.FileName is not null))
                            continue;
                        Fields[name] = await part.ReadAsStringAsync(cancellationToken);
                    }
                }

                if (failWith is { } status)
                    return new HttpResponseMessage(status);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(wavBody),
                };
            }
        }
    }
}
