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
    public class Qwen3ParagraphTtsClientTests
    {
        private static readonly ParagraphTtsServiceConfig Config = new()
        {
            Name = "test",
            Type = ParagraphTtsServiceType.Qwen3Base,
            SettingsJson = """{"baseUrl":"http://test","language":"en"}""",
        };

        private static byte[] FakeWav() =>
            Encoding.ASCII.GetBytes("RIFF____WAVEfmt ");

        [Fact]
        public async Task GenerateAsync_PostsMultipartToTtsEndpoint_WithTextLanguageAndTranscript()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(Encoding.UTF8.GetBytes("fake-wav"));
            await sut.GenerateAsync("hello world", "ignored instructions", refAudio, Config, null, referenceTranscript: "the sample text");

            Assert.Single(handler.Requests);
            var req = handler.Requests[0];
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.EndsWith("/tts", req.RequestUri!.AbsolutePath);

            Assert.Equal("hello world", handler.Fields["text"]);
            Assert.Equal("en", handler.Fields["language"]);
            Assert.Equal("the sample text", handler.Fields["voice_transcript"]);
        }

        [Fact]
        public async Task GenerateAsync_WithNullReferenceTranscript_ThrowsWithoutCallingService()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.GenerateAsync("text", null, refAudio, Config, null));

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task GenerateAsync_OmitsNullSamplingParams_SendsOnlyNonNullOnes()
        {
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await sut.GenerateAsync("text", null, refAudio, Config, null, referenceTranscript: "sample");

            Assert.False(handler.Fields.ContainsKey("temperature"));
            Assert.False(handler.Fields.ContainsKey("top_p"));
            Assert.False(handler.Fields.ContainsKey("top_k"));
            Assert.False(handler.Fields.ContainsKey("repetition_penalty"));
            Assert.False(handler.Fields.ContainsKey("max_new_tokens"));
        }

        [Fact]
        public async Task GenerateAsync_WithSamplingParamsSet_SendsThemInvariantCultureFormatted()
        {
            var configWithSampling = new ParagraphTtsServiceConfig
            {
                Name = "test",
                Type = ParagraphTtsServiceType.Qwen3Base,
                SettingsJson = """{"baseUrl":"http://test","language":"en","temperature":0.7,"top_p":0.9,"top_k":40,"repetition_penalty":1.1,"max_new_tokens":512}""",
            };
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await sut.GenerateAsync("text", null, refAudio, configWithSampling, null, referenceTranscript: "sample");

            Assert.Equal((0.7).ToString(CultureInfo.InvariantCulture), handler.Fields["temperature"]);
            Assert.Equal((0.9).ToString(CultureInfo.InvariantCulture), handler.Fields["top_p"]);
            Assert.Equal("40", handler.Fields["top_k"]);
            Assert.Equal((1.1).ToString(CultureInfo.InvariantCulture), handler.Fields["repetition_penalty"]);
            Assert.Equal("512", handler.Fields["max_new_tokens"]);
        }

        [Fact]
        public async Task GenerateAsync_WithApiKey_SendsBearerAuthorizationHeader()
        {
            var configWithKey = new ParagraphTtsServiceConfig
            {
                Name = "test",
                Type = ParagraphTtsServiceType.Qwen3Base,
                SettingsJson = """{"baseUrl":"http://test","apiKey":"secret-key"}""",
            };
            var handler = new FakeHttpMessageHandler(FakeWav());
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            await sut.GenerateAsync("text", null, refAudio, configWithKey, null, referenceTranscript: "sample");

            Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
            Assert.Equal("secret-key", handler.Requests[0].Headers.Authorization?.Parameter);
        }

        [Fact]
        public async Task GenerateAsync_ReturnsWavStream_PassThrough()
        {
            var wav = FakeWav();
            var handler = new FakeHttpMessageHandler(wav);
            var factory = new FakeHttpClientFactory(handler);
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, new FakeAiServiceReporter());

            using var refAudio = new MemoryStream(new byte[4]);
            var result = await sut.GenerateAsync("text", null, refAudio, Config, null, referenceTranscript: "sample");

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
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, reporter);

            using var refAudio = new MemoryStream(new byte[4]);
            await Assert.ThrowsAsync<AiServiceUnavailableException>(() =>
                sut.GenerateAsync("text", null, refAudio, Config, null, referenceTranscript: "sample"));

            Assert.Single(reporter.Failures);
        }

        [Fact]
        public async Task GenerateAsync_OnHttpFailure_UnmanagedService_RethrowsOriginal()
        {
            var handler = new FakeHttpMessageHandler(FakeWav(), failWith: HttpStatusCode.InternalServerError);
            var factory = new FakeHttpClientFactory(handler);
            var reporter = new FakeAiServiceReporter { Managed = false };
            var sut = new Qwen3ParagraphTtsClient(factory, NullLogger<Qwen3ParagraphTtsClient>.Instance, reporter);

            using var refAudio = new MemoryStream(new byte[4]);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                sut.GenerateAsync("text", null, refAudio, Config, null, referenceTranscript: "sample"));
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
