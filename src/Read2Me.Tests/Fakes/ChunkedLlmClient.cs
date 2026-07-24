using System.Runtime.CompilerServices;
using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scripted <see cref="ILlmClient"/> yielding an exact chunk sequence, for exercising the
    /// completion runner's streaming envelope. Unlike <see cref="FakeLlmClient"/> (one content
    /// blob per call) this fake scripts individual thinking/content chunks and mid-stream throws,
    /// and counts chunks actually pulled so tests can assert early stream-break.
    /// </summary>
    public sealed class ChunkedLlmClient : ILlmClient
    {
        private sealed record Step(LlmChatChunk? Chunk, Exception? Throws);
        private readonly List<Step> _script = [];

        /// <summary>Chunks the consumer actually pulled (enumeration advances).</summary>
        public int ChunksPulled { get; private set; }

        public List<(LlmServerConfig Config, string Prompt, string? Schema, bool DisableThinking)> Calls { get; } = [];

        public ChunkedLlmClient Content(params string[] chunks)
        {
            foreach (var c in chunks)
                _script.Add(new Step(new LlmChatChunk(null, c, false), null));
            return this;
        }

        public ChunkedLlmClient Thinking(params string[] chunks)
        {
            foreach (var t in chunks)
                _script.Add(new Step(new LlmChatChunk(t, null, false), null));
            return this;
        }

        /// <summary>
        /// Scripts a delta-less metrics chunk, as llama.cpp emits with <c>timings_per_token</c>
        /// and <c>include_usage</c> on: no text, just root-level server measurements.
        /// </summary>
        public ChunkedLlmClient Metrics(LlmTimings? timings = null, LlmUsage? usage = null)
        {
            _script.Add(new Step(new LlmChatChunk(null, null, false, timings, usage), null));
            return this;
        }

        public ChunkedLlmClient Throws(Exception ex)
        {
            _script.Add(new Step(null, ex));
            return this;
        }

        public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt, string? jsonSchema = null,
            bool disableThinking = false,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Add((config, prompt, jsonSchema, disableThinking));
            foreach (var step in _script)
            {
                if (step.Throws != null) throw step.Throws;
                ChunksPulled++;
                yield return step.Chunk!;
            }
            yield return new LlmChatChunk(null, null, Done: true);
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
