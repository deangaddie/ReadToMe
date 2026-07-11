using System.Runtime.CompilerServices;
using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scripted <see cref="ILlmClient"/> that records the <see cref="LlmServerConfig"/> used on each
    /// call and can script responses per config, so different chain steps return different answers.
    /// Distinct from <see cref="FakeLlmClient"/> (which serves responses positionally regardless of
    /// config) — this fake keys behaviour on the config's <c>Name</c>.
    /// </summary>
    public sealed class SequenceLlmClient : ILlmClient
    {
        // Per-config response scripts. Each config name maps to a queue of responses served in order
        // (the last response repeats once the queue is exhausted). A queued Exception throws instead.
        private readonly Dictionary<string, Queue<Response>> _byConfig = new(StringComparer.Ordinal);

        /// <summary>Config used on each call, in call order.</summary>
        public List<LlmServerConfig> Configs { get; } = [];

        /// <summary>(config, prompt) recorded per call, in call order.</summary>
        public List<(LlmServerConfig Config, string Prompt)> Calls { get; } = [];

        private sealed record Response(string? Text, Exception? Throws);

        /// <summary>Script one or more responses for calls made with the config of this name.</summary>
        public SequenceLlmClient ForConfig(string configName, params string[] responses)
        {
            var q = GetQueue(configName);
            foreach (var r in responses)
                q.Enqueue(new Response(r, null));
            return this;
        }

        /// <summary>Script a throw for the next call made with the config of this name.</summary>
        public SequenceLlmClient ThrowFor(string configName, Exception ex)
        {
            GetQueue(configName).Enqueue(new Response(null, ex));
            return this;
        }

        private Queue<Response> GetQueue(string configName)
        {
            if (!_byConfig.TryGetValue(configName, out var q))
                _byConfig[configName] = q = new Queue<Response>();
            return q;
        }

        public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt, string? jsonSchema = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Configs.Add(config);
            Calls.Add((config, prompt));

            var response = Next(config.Name);
            if (response.Throws != null) throw response.Throws;

            yield return new LlmChatChunk(null, response.Text, false);
            yield return new LlmChatChunk(null, null, Done: true);
            await Task.CompletedTask;
        }

        private Response Next(string configName)
        {
            var q = GetQueue(configName);
            if (q.Count == 0)
                return new Response(string.Empty, null);
            // Last response repeats: peek-and-keep when only one remains.
            return q.Count == 1 ? q.Peek() : q.Dequeue();
        }

        public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
