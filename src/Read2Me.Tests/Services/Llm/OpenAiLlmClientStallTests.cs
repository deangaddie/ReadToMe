using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class OpenAiLlmClientStallTests
    {
        private static readonly LlmServerConfig Config =
            new() { Name = "Test", BaseUrl = "http://localhost:8080", Model = "m" };

        private static OpenAiLlmClient NewClient(Stream responseBody, int inactivitySeconds)
        {
            var handler = new StreamingHandler(responseBody);
            var factory = new SingleClientFactory(new HttpClient(handler));
            var options = Options.Create(new AiWatchdogOptions { StreamInactivitySeconds = inactivitySeconds });
            return new OpenAiLlmClient(factory, NullLogger<OpenAiLlmClient>.Instance, options);
        }

        private static byte[] Line(string s) => Encoding.UTF8.GetBytes(s + "\n");

        private static byte[] ContentLine(string text) =>
            Line("data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}");

        [Fact]
        public async Task SilentStreamPastWindow_ThrowsStalled()
        {
            // No chunk ever arrives; the read blocks past the 1s inactivity window.
            var body = new ScriptedStream(Array.Empty<(TimeSpan, byte[])>(), blockAtEnd: true);
            var client = NewClient(body, inactivitySeconds: 1);

            var ex = await Assert.ThrowsAsync<AiServiceStalledException>(async () =>
            {
                await foreach (var _ in client.StreamChatAsync(Config, "prompt", ct: CancellationToken.None)) { }
            });

            Assert.Equal(Config.BaseUrl, ex.BaseUrl);
        }

        [Fact]
        public async Task SteadyChunksSlowerThanWindow_DoNotStall()
        {
            // Each line arrives 250ms apart — under the 1s window — so the sliding timeout keeps resetting.
            var chunks = new (TimeSpan, byte[])[]
            {
                (TimeSpan.FromMilliseconds(250), ContentLine("a")),
                (TimeSpan.FromMilliseconds(250), ContentLine("b")),
                (TimeSpan.FromMilliseconds(250), ContentLine("c")),
                (TimeSpan.FromMilliseconds(250), Line("data: [DONE]")),
            };
            var body = new ScriptedStream(chunks, blockAtEnd: false);
            var client = NewClient(body, inactivitySeconds: 1);

            var content = new StringBuilder();
            await foreach (var chunk in client.StreamChatAsync(Config, "prompt", ct: CancellationToken.None))
            {
                if (chunk.Content is { } c) content.Append(c);
            }

            Assert.Equal("abc", content.ToString());
        }

        // ---- Fakes ----------------------------------------------------------

        private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }

        private sealed class StreamingHandler(Stream body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(body),
                });
        }

        /// <summary>
        /// A read stream that yields scripted chunks, each after an optional delay (honouring the read
        /// cancellation token), then either EOFs or blocks forever — used to drive the inactivity window.
        /// </summary>
        private sealed class ScriptedStream(
            IReadOnlyList<(TimeSpan Delay, byte[] Data)> chunks, bool blockAtEnd) : Stream
        {
            private int _index;
            private byte[]? _current;
            private int _offset;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_current is null || _offset >= _current.Length)
                {
                    if (_index >= chunks.Count)
                    {
                        if (blockAtEnd) await Task.Delay(Timeout.Infinite, ct);
                        return 0;
                    }
                    var (delay, data) = chunks[_index++];
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                    _current = data;
                    _offset = 0;
                }

                int n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
