using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.VoiceDesign;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoxCpm2VoiceDesignClientTests
    {
        private readonly FakeHttpClientFactory _httpFactory;
        private readonly VoxCpm2VoiceDesignClient _sut;

        public VoxCpm2VoiceDesignClientTests()
        {
            _httpFactory = new FakeHttpClientFactory();
            _sut = new VoxCpm2VoiceDesignClient(_httpFactory, NullLogger<VoxCpm2VoiceDesignClient>.Instance);
        }

        [Fact]
        public async Task Parse_MetaThenPcmThenDone_ProducesWavAtSampleRate()
        {
            // meta: {"type":"meta","sample_rate":24000}
            var metaJson = "{\"type\":\"meta\",\"sample_rate\":24000}";
            var metaPayload = Encoding.UTF8.GetBytes(metaJson);
            var metaFrame = CreateFrame(0, metaPayload);

            // pcm: 2 float32 samples (1.0, -1.0)
            var pcmData = new byte[8];
            BitConverter.TryWriteBytes(pcmData.AsSpan(0, 4), 1.0f);
            BitConverter.TryWriteBytes(pcmData.AsSpan(4, 4), -1.0f);
            var pcmFrame = CreateFrame(1, pcmData);

            // done: {"type":"done"}
            var doneJson = "{\"type\":\"done\"}";
            var donePayload = Encoding.UTF8.GetBytes(doneJson);
            var doneFrame = CreateFrame(0, donePayload);

            var streamData = new byte[metaFrame.Length + pcmFrame.Length + doneFrame.Length];
            metaFrame.CopyTo(streamData, 0);
            pcmFrame.CopyTo(streamData, metaFrame.Length);
            doneFrame.CopyTo(streamData, metaFrame.Length + pcmFrame.Length);

            SetupHttp(HttpStatusCode.OK, streamData);

            var config = new VoiceDesignServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var result = await _sut.DesignVoiceAsync(config, "prompt", "text", null);

            Assert.NotNull(result);
            var wav = new byte[result.Length];
            await result.ReadExactlyAsync(wav);
            
            // Check RIFF header
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            // Check sample rate at offset 24
            Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
        }

        [Fact]
        public async Task Parse_ErrorFrame_Throws()
        {
            var errorJson = "{\"type\":\"error\",\"message\":\"BOOM\"}";
            var errorPayload = Encoding.UTF8.GetBytes(errorJson);
            var errorFrame = CreateFrame(0, errorPayload);

            SetupHttp(HttpStatusCode.OK, errorFrame);

            var config = new VoiceDesignServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
                _sut.DesignVoiceAsync(config, "prompt", "text", null));
            Assert.Equal("BOOM", ex.Message);
        }

        [Fact]
        public async Task Parse_TruncatedHeader_Throws()
        {
            var truncated = new byte[] { 0, 1, 0, 0 }; // only 4 bytes
            SetupHttp(HttpStatusCode.OK, truncated);

            var config = new VoiceDesignServiceConfig { SettingsJson = "{\"BaseUrl\":\"http://test\"}" };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => 
                _sut.DesignVoiceAsync(config, "prompt", "text", null));
            Assert.Equal("Truncated frame header.", ex.Message);
        }

        private byte[] CreateFrame(byte type, byte[] payload)
        {
            var frame = new byte[5 + payload.Length];
            frame[0] = type;
            BitConverter.TryWriteBytes(frame.AsSpan(1, 4), (uint)payload.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(frame, 1, 4);
            payload.CopyTo(frame, 5);
            return frame;
        }

        private void SetupHttp(HttpStatusCode code, byte[] content)
        {
            _httpFactory.Response = new HttpResponseMessage(code)
            {
                Content = new ByteArrayContent(content)
            };
        }

        private class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpResponseMessage? Response { get; set; }
            public HttpClient CreateClient(string name) => new HttpClient(new FakeHttpMessageHandler(Response!));
        }

        private class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(response);
        }
    }
}
