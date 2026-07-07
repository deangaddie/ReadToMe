using System.Runtime.CompilerServices;
using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>Canned-response ILlmClient. Multiple responses are served one per call
    /// (last one repeats); a configured exception throws on every call.</summary>
    public sealed class FakeLlmClient(params string[] responses) : ILlmClient
    {
        private int _calls;

        public Exception? Throws { get; init; }
        public int CallCount => _calls;
        public List<string> Prompts { get; } = [];

        public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt, string? jsonSchema = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var call = _calls++;
            Prompts.Add(prompt);
            if (Throws != null) throw Throws;
            var response = responses.Length == 0
                ? string.Empty
                : responses[Math.Min(call, responses.Length - 1)];
            yield return new LlmChatChunk(null, response, false);
            yield return new LlmChatChunk(null, null, Done: true);
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
