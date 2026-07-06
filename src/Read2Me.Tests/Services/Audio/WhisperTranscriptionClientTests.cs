using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WhisperTranscriptionClientTests
    {
        private readonly FakeHttpClientFactory _httpFactory;
        private readonly FakeAiServiceReporter _reporter = new();
        private readonly WhisperTranscriptionClient _sut;

        public WhisperTranscriptionClientTests()
        {
            _httpFactory = new FakeHttpClientFactory();
            _sut = new WhisperTranscriptionClient(_httpFactory, NullLogger<WhisperTranscriptionClient>.Instance, _reporter);
        }

        [Fact]
        public async Task ManagedServiceFailure_ReportsAndThrowsServiceUnavailable()
        {
            _reporter.Managed = true; // base URL resolves to a docker service
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://localhost:9000\"}" };

            await Assert.ThrowsAsync<Read2Me.Services.Health.AiServiceUnavailableException>(
                () => _sut.TranscribeAsync(config, new MemoryStream([1]), "a.wav"));

            var (baseUrl, _) = Assert.Single(_reporter.Failures);
            Assert.Equal("http://localhost:9000", baseUrl);
        }

        [Fact]
        public async Task RemoteServiceFailure_StaysSilent_PropagatesOriginal()
        {
            _reporter.Managed = false; // remote endpoint — registry miss
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"https://remote.example.com\"}" };

            await Assert.ThrowsAsync<HttpRequestException>(
                () => _sut.TranscribeAsync(config, new MemoryStream([1]), "a.wav"));
        }

        [Fact]
        public async Task Transcribe_PostsAudio_ReturnsTranscriptText()
        {
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("  Transcribed text  ")
            };

            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var audio = new MemoryStream([1, 2, 3]);
            var result = await _sut.TranscribeAsync(config, audio, "test.wav");

            Assert.Equal("Transcribed text", result);
            Assert.NotNull(_httpFactory.LastRequest);
            Assert.Equal(HttpMethod.Post, _httpFactory.LastRequest.Method);
            Assert.Contains("/asr?task=transcribe&output=txt", _httpFactory.LastRequest.RequestUri?.ToString() ?? "");
            
            Assert.IsType<MultipartFormDataContent>(_httpFactory.LastRequest.Content);
            var strContent = _httpFactory.LastRequestContent ?? "";
            Assert.Contains("audio_file", strContent);
            Assert.Contains("test.wav", strContent);
        }

        [Fact]
        public async Task TranscribeWithWordTimestamps_RequestsJsonOutput_FlattensSegmentWords()
        {
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "text": "hello world again",
                      "segments": [
                        { "words": [
                            { "word": " hello", "start": 0.0, "end": 0.4 },
                            { "word": " world", "start": 0.5, "end": 0.9 }
                        ] },
                        { "words": [
                            { "word": " again", "start": 1.2, "end": 1.6 }
                        ] }
                      ]
                    }
                    """)
            };

            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var words = await _sut.TranscribeWithWordTimestampsAsync(config, new MemoryStream([1]), "test.wav");

            Assert.Contains("/asr?task=transcribe&output=json&word_timestamps=true",
                _httpFactory.LastRequest?.RequestUri?.ToString() ?? "");
            Assert.IsType<MultipartFormDataContent>(_httpFactory.LastRequest?.Content);
            Assert.Contains("audio_file", _httpFactory.LastRequestContent ?? "");

            Assert.Equal(3, words.Count);
            Assert.Equal(new TranscribedWord(" hello", 0.0, 0.4), words[0]);
            Assert.Equal(new TranscribedWord(" world", 0.5, 0.9), words[1]);
            Assert.Equal(new TranscribedWord(" again", 1.2, 1.6), words[2]);
        }

        [Fact]
        public async Task TranscribeWithWordTimestamps_SegmentWithoutWords_IsSkipped()
        {
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "segments": [ { "text": "no words here" }, { "words": [] } ] }
                    """)
            };

            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var words = await _sut.TranscribeWithWordTimestampsAsync(config, new MemoryStream([1]), "test.wav");

            Assert.Empty(words);
        }

        [Fact]
        public async Task TranscribeWithWordTimestamps_MissingSegments_ReturnsEmpty()
        {
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "text": "hello" }""")
            };

            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var words = await _sut.TranscribeWithWordTimestampsAsync(config, new MemoryStream([1]), "test.wav");

            Assert.Empty(words);
        }

        [Fact]
        public async Task TranscribeWithWordTimestamps_ManagedServiceFailure_ThrowsServiceUnavailable()
        {
            _reporter.Managed = true;
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var config = new TranscriptionServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://localhost:9000\"}" };

            await Assert.ThrowsAsync<Read2Me.Services.Health.AiServiceUnavailableException>(
                () => _sut.TranscribeWithWordTimestampsAsync(config, new MemoryStream([1]), "a.wav"));
        }

        private class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpResponseMessage? Response { get; set; }
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestContent { get; private set; }
            public HttpClient CreateClient(string name) => new HttpClient(new FakeHttpMessageHandler(this));

            private class FakeHttpMessageHandler(FakeHttpClientFactory factory) : HttpMessageHandler
            {
                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    factory.LastRequest = request;
                    if (request.Content != null)
                    {
                        factory.LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
                    }
                    return factory.Response!;
                }
            }
        }
    }
}
