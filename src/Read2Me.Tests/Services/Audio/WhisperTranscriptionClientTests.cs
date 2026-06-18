using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class WhisperTranscriptionClientTests
    {
        private readonly FakeHttpClientFactory _httpFactory;
        private readonly WhisperTranscriptionClient _sut;

        public WhisperTranscriptionClientTests()
        {
            _httpFactory = new FakeHttpClientFactory();
            _sut = new WhisperTranscriptionClient(_httpFactory, NullLogger<WhisperTranscriptionClient>.Instance);
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
            
            var content = Assert.IsType<MultipartFormDataContent>(_httpFactory.LastRequest.Content);
            var strContent = await content.ReadAsStringAsync();
            Assert.Contains("audio_file", strContent);
            Assert.Contains("test.wav", strContent);
        }

        private class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpResponseMessage? Response { get; set; }
            public HttpRequestMessage? LastRequest { get; private set; }
            public HttpClient CreateClient(string name) => new HttpClient(new FakeHttpMessageHandler(this));

            private class FakeHttpMessageHandler(FakeHttpClientFactory factory) : HttpMessageHandler
            {
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    factory.LastRequest = request;
                    return Task.FromResult(factory.Response!);
                }
            }
        }
    }
}
