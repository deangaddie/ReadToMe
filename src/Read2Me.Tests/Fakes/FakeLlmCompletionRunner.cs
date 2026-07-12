using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scripted <see cref="ILlmCompletionRunner"/>. Steps are served in order (the last repeats).
    /// A Completed step applies the caller's parser to the scripted raw, mirroring the real
    /// runner's contract (parse rejection becomes <see cref="LlmRunOutcome.ParseFailed"/>);
    /// failure steps come back as-is without invoking the parser. Records every request.
    /// </summary>
    public sealed class FakeLlmCompletionRunner : ILlmCompletionRunner
    {
        private sealed record Step(LlmRunOutcome Outcome, string Raw, string? Error, Exception? Throws);
        private readonly List<Step> _steps = [];
        private int _calls;

        public List<LlmRunRequest> Requests { get; } = [];

        public FakeLlmCompletionRunner Completes(string raw)
        {
            _steps.Add(new Step(LlmRunOutcome.Completed, raw, null, null));
            return this;
        }

        public FakeLlmCompletionRunner Fails(LlmRunOutcome outcome, string error)
        {
            _steps.Add(new Step(outcome, string.Empty, error, null));
            return this;
        }

        public FakeLlmCompletionRunner Throws(Exception ex)
        {
            _steps.Add(new Step(default, string.Empty, null, ex));
            return this;
        }

        private Step Next()
        {
            var i = Math.Min(_calls++, _steps.Count - 1);
            return i < 0 ? new Step(LlmRunOutcome.Completed, string.Empty, null, null) : _steps[i];
        }

        public Task<LlmRunResult<T>> RunAsync<T>(LlmRunRequest request, TryParse<T> parser, CancellationToken ct)
        {
            Requests.Add(request);
            var step = Next();
            if (step.Throws != null) throw step.Throws;
            if (step.Outcome != LlmRunOutcome.Completed)
                return Task.FromResult(new LlmRunResult<T>(step.Outcome, default, step.Raw, step.Error));
            if (!parser(step.Raw, out var value, out var error))
                return Task.FromResult(new LlmRunResult<T>(LlmRunOutcome.ParseFailed, value, step.Raw, error));
            return Task.FromResult(new LlmRunResult<T>(LlmRunOutcome.Completed, value, step.Raw, null));
        }

        public Task<LlmRunResult<string>> RunAsync(LlmRunRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var step = Next();
            if (step.Throws != null) throw step.Throws;
            return Task.FromResult(step.Outcome == LlmRunOutcome.Completed
                ? new LlmRunResult<string>(LlmRunOutcome.Completed, step.Raw, step.Raw, null)
                : new LlmRunResult<string>(step.Outcome, null, step.Raw, step.Error));
        }
    }
}
