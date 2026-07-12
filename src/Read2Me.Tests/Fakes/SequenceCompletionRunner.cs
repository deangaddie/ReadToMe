using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scripted <see cref="ILlmCompletionRunner"/> keyed on the request config's <c>Name</c>, so
    /// different chain steps return different answers — the runner-seam sibling of
    /// <see cref="SequenceLlmClient"/>. Each config name maps to a queue of steps served in order
    /// (the last repeats). A raw-text step applies the caller's parser (parse rejection becomes
    /// <see cref="LlmRunOutcome.ParseFailed"/>, mirroring the real runner); a failure step comes
    /// back as-is; a throw step throws. Records the config and full request per call.
    /// </summary>
    public sealed class SequenceCompletionRunner : ILlmCompletionRunner
    {
        private sealed record Step(string? Raw, LlmRunOutcome Outcome, string? Error, Exception? Throws);
        private readonly Dictionary<string, Queue<Step>> _byConfig = new(StringComparer.Ordinal);

        /// <summary>Config used on each call, in call order.</summary>
        public List<LlmServerConfig> Configs { get; } = [];

        /// <summary>(config, prompt) recorded per call, in call order.</summary>
        public List<(LlmServerConfig Config, string Prompt)> Calls { get; } = [];

        /// <summary>Script one or more raw completions for calls made with the config of this name.</summary>
        public SequenceCompletionRunner ForConfig(string configName, params string[] responses)
        {
            var q = GetQueue(configName);
            foreach (var r in responses)
                q.Enqueue(new Step(r, LlmRunOutcome.Completed, null, null));
            return this;
        }

        /// <summary>Script a non-Completed run outcome for the next call made with this config.</summary>
        public SequenceCompletionRunner FailFor(string configName, LlmRunOutcome outcome, string error)
        {
            GetQueue(configName).Enqueue(new Step(null, outcome, error, null));
            return this;
        }

        /// <summary>Script a throw (e.g. genuine cancellation) for the next call made with this config.</summary>
        public SequenceCompletionRunner ThrowFor(string configName, Exception ex)
        {
            GetQueue(configName).Enqueue(new Step(null, default, null, ex));
            return this;
        }

        private Queue<Step> GetQueue(string configName)
        {
            if (!_byConfig.TryGetValue(configName, out var q))
                _byConfig[configName] = q = new Queue<Step>();
            return q;
        }

        private Step Next(string configName)
        {
            var q = GetQueue(configName);
            if (q.Count == 0)
                return new Step(string.Empty, LlmRunOutcome.Completed, null, null);
            // Last step repeats: peek-and-keep when only one remains.
            return q.Count == 1 ? q.Peek() : q.Dequeue();
        }

        public Task<LlmRunResult<T>> RunAsync<T>(LlmRunRequest request, TryParse<T> parser, CancellationToken ct)
        {
            var step = Record(request);
            if (step.Throws != null) throw step.Throws;
            if (step.Outcome != LlmRunOutcome.Completed)
                return Task.FromResult(new LlmRunResult<T>(step.Outcome, default, string.Empty, step.Error));
            if (!parser(step.Raw!, out var value, out var error))
                return Task.FromResult(new LlmRunResult<T>(LlmRunOutcome.ParseFailed, value, step.Raw!, error));
            return Task.FromResult(new LlmRunResult<T>(LlmRunOutcome.Completed, value, step.Raw!, null));
        }

        public Task<LlmRunResult<string>> RunAsync(LlmRunRequest request, CancellationToken ct)
        {
            var step = Record(request);
            if (step.Throws != null) throw step.Throws;
            return Task.FromResult(step.Outcome == LlmRunOutcome.Completed
                ? new LlmRunResult<string>(LlmRunOutcome.Completed, step.Raw, step.Raw!, null)
                : new LlmRunResult<string>(step.Outcome, null, string.Empty, step.Error));
        }

        private Step Record(LlmRunRequest request)
        {
            Configs.Add(request.Config);
            Calls.Add((request.Config, request.Prompt));
            return Next(request.Config.Name);
        }
    }
}
