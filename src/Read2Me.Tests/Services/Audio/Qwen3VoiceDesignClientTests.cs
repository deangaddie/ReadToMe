using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class Qwen3VoiceDesignClientTests
    {
        private readonly FakeHttpClientFactory _httpFactory;
        private readonly Qwen3VoiceDesignClient _sut;

        public Qwen3VoiceDesignClientTests()
        {
            _httpFactory = new FakeHttpClientFactory();
            _sut = new Qwen3VoiceDesignClient(_httpFactory, NullLogger<Qwen3VoiceDesignClient>.Instance, new FakeAiServiceReporter());
        }

        [Fact]
        public async Task Design_PostsMultipartForm_WithTextAndVoiceDescription()
        {
            var audioData = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioData)
            };

            var config = new VoiceDesignServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var result = await _sut.DesignVoiceAsync(config, "prompt", "text", null);

            Assert.NotNull(result);
            var written = new byte[result.Length];
            await result.ReadExactlyAsync(written);
            Assert.Equal(audioData, written);

            Assert.NotNull(_httpFactory.LastRequest);
            Assert.Equal(HttpMethod.Post, _httpFactory.LastRequest.Method);
            Assert.Equal("http://test/tts", _httpFactory.LastRequest.RequestUri?.ToString());
            
            var content = Assert.IsType<MultipartFormDataContent>(_httpFactory.LastRequest.Content);
            // Can't easily inspect MultipartFormDataContent parts without reading it back
            var strContent = await content.ReadAsStringAsync();
            Assert.Contains("text", strContent);
            Assert.Contains("voice_description", strContent);
        }

        [Fact]
        public async Task Design_SendsLanguageAndNonNullSamplingParams()
        {
            var audioData = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // RIFF
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioData)
            };

            var config = new VoiceDesignServiceConfig
            {
                SettingsJson = "{\"BaseUrl\":\"http://test\",\"Language\":\"en\",\"Temperature\":0.7,\"TopK\":40}"
            };
            await _sut.DesignVoiceAsync(config, "prompt", "text", null);

            var content = Assert.IsType<MultipartFormDataContent>(_httpFactory.LastRequest!.Content);
            var strContent = await content.ReadAsStringAsync();
            Assert.Contains("language", strContent);
            Assert.Contains("en", strContent);
            Assert.Contains("temperature", strContent);
            Assert.Contains("0.7", strContent);
            Assert.Contains("top_k", strContent);
            Assert.Contains("40", strContent);
            Assert.DoesNotContain("top_p", strContent);
            Assert.DoesNotContain("repetition_penalty", strContent);
            Assert.DoesNotContain("max_new_tokens", strContent);
        }

        [Fact]
        public async Task Design_NonSuccessStatus_Throws()
        {
            _httpFactory.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

            var config = new VoiceDesignServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            await Assert.ThrowsAsync<HttpRequestException>(() => 
                _sut.DesignVoiceAsync(config, "prompt", "text", null));
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
