using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoxCpm2ParagraphTtsClientTests
    {
        private readonly FakeHttpMessageHandler _handler;
        private readonly FakeHttpClientFactory _httpFactory;
        private readonly VoxCpm2ParagraphTtsClient _sut;

        private static readonly ParagraphTtsServiceConfig Config = new()
        {
            Name = "test",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = """{"BaseUrl":"http://test","MaxLen":4096}""",
        };

        public VoxCpm2ParagraphTtsClientTests()
        {
            _handler = new FakeHttpMessageHandler();
            _httpFactory = new FakeHttpClientFactory(_handler);
            _sut = new VoxCpm2ParagraphTtsClient(
                _httpFactory,
                NullLogger<VoxCpm2ParagraphTtsClient>.Instance);
        }

        // ── Frame protocol ──────────────────────────────────────────────────

        [Fact]
        public async Task Parse_MetaThenPcmThenDone_ProducesWavAtSampleRate()
        {
            var streamFrames = BuildFrames(
                MetaFrame(24000),
                PcmFrame(1.0f, -1.0f),
                DoneFrame());

            _handler.SetupUploadThenStream(fileId: "abc", streamBody: streamFrames);

            using var refAudio = new MemoryStream(new byte[4]);
            var result = await _sut.GenerateAsync("hello", null, refAudio, Config);

            Assert.NotNull(result);
            var wav = new byte[result.Length];
            await result.ReadExactlyAsync(wav);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
        }

        [Fact]
        public async Task Parse_ErrorFrame_Throws()
        {
            var streamFrames = BuildFrames(ErrorFrame("BOOM"));
            _handler.SetupUploadThenStream(fileId: "x", streamBody: streamFrames);

            using var refAudio = new MemoryStream(new byte[4]);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _sut.GenerateAsync("hello", null, refAudio, Config));
            Assert.Equal("BOOM", ex.Message);
        }

        [Fact]
        public async Task Parse_TruncatedHeader_Throws()
        {
            var truncated = new byte[] { 0, 1, 0, 0 }; // only 4 bytes
            _handler.SetupUploadThenStream(fileId: "x", streamBody: truncated);

            using var refAudio = new MemoryStream(new byte[4]);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _sut.GenerateAsync("hello", null, refAudio, Config));
            Assert.Equal("Truncated frame header.", ex.Message);
        }

        // ── HTTP call sequence ───────────────────────────────────────────────

        [Fact]
        public async Task GenerateAsync_UploadsReferenceAudioFirst_ThenStreams()
        {
            var streamFrames = BuildFrames(MetaFrame(24000), DoneFrame());
            _handler.SetupUploadThenStream(fileId: "file-42", streamBody: streamFrames);

            using var refAudio = new MemoryStream(Encoding.UTF8.GetBytes("fake-wav"));
            await _sut.GenerateAsync("text", "control instructions", refAudio, Config);

            Assert.Equal(2, _handler.Requests.Count);

            var uploadReq = _handler.Requests[0];
            Assert.Equal(HttpMethod.Post, uploadReq.Method);
            Assert.EndsWith("/upload-audio", uploadReq.RequestUri!.AbsolutePath);
            Assert.IsType<MultipartFormDataContent>(uploadReq.Content);

            var streamReq = _handler.Requests[1];
            Assert.Equal(HttpMethod.Post, streamReq.Method);
            Assert.EndsWith("/api/stream", streamReq.RequestUri!.AbsolutePath);
        }

        [Fact]
        public async Task GenerateAsync_StreamRequestBody_ContainsFileIdAsReferenceWavPath()
        {
            var streamFrames = BuildFrames(MetaFrame(24000), DoneFrame());
            _handler.SetupUploadThenStream(fileId: "returned-file-id", streamBody: streamFrames);

            using var refAudio = new MemoryStream(new byte[4]);
            await _sut.GenerateAsync("the text", "my control", refAudio, Config);

            var body = _handler.RequestBodies[1];
            Assert.NotNull(body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Assert.Equal("the text", root.GetProperty("text").GetString());
            Assert.Equal("my control", root.GetProperty("control").GetString());
            Assert.Equal("returned-file-id", root.GetProperty("reference_wav_path").GetString());
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static byte[] BuildFrames(params byte[][] frames)
        {
            int total = 0;
            foreach (var f in frames) total += f.Length;
            var buf = new byte[total];
            int offset = 0;
            foreach (var f in frames) { f.CopyTo(buf, offset); offset += f.Length; }
            return buf;
        }

        private static byte[] MetaFrame(int sampleRate)
        {
            var json = $"{{\"type\":\"meta\",\"sample_rate\":{sampleRate}}}";
            return CreateFrame(0, Encoding.UTF8.GetBytes(json));
        }

        private static byte[] PcmFrame(params float[] samples)
        {
            var data = new byte[samples.Length * 4];
            for (int i = 0; i < samples.Length; i++)
                BitConverter.TryWriteBytes(data.AsSpan(i * 4, 4), samples[i]);
            return CreateFrame(1, data);
        }

        private static byte[] DoneFrame()
        {
            var json = "{\"type\":\"done\"}";
            return CreateFrame(0, Encoding.UTF8.GetBytes(json));
        }

        private static byte[] ErrorFrame(string message)
        {
            var json = $"{{\"type\":\"error\",\"message\":\"{message}\"}}";
            return CreateFrame(0, Encoding.UTF8.GetBytes(json));
        }

        private static byte[] CreateFrame(byte type, byte[] payload)
        {
            var frame = new byte[5 + payload.Length];
            frame[0] = type;
            BitConverter.TryWriteBytes(frame.AsSpan(1, 4), (uint)payload.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(frame, 1, 4);
            payload.CopyTo(frame, 5);
            return frame;
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        private class FakeHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new HttpClient(handler);
        }

        private class FakeHttpMessageHandler : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = new();
            public List<string?> RequestBodies { get; } = new();
            private string _fileId = "test-file-id";
            private byte[] _streamBody = Array.Empty<byte>();

            public void SetupUploadThenStream(string fileId, byte[] streamBody)
            {
                _fileId = fileId;
                _streamBody = streamBody;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Buffer body before request is disposed
                string? body = null;
                if (request.Content is StringContent)
                    body = await request.Content.ReadAsStringAsync(cancellationToken);
                RequestBodies.Add(body);
                Requests.Add(request);

                if (request.RequestUri!.AbsolutePath.EndsWith("/upload-audio"))
                {
                    var json = JsonSerializer.Serialize(new { file_id = _fileId });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                // /api/stream
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_streamBody),
                };
            }
        }
    }
}
