using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Events;
using Read2Me.Services.Health;

namespace Read2Me.Services.Llm
{
    public sealed class LlmCompletionRunner(
        ILlmClient llm,
        IAiServiceReporter reporter,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        ILogger<LlmCompletionRunner> logger) : ILlmCompletionRunner
    {
        public async Task<LlmRunResult<T>> RunAsync<T>(LlmRunRequest request, TryParse<T> parser, CancellationToken ct)
        {
            var run = await StreamAsync(request, ct);
            if (run.Outcome != LlmRunOutcome.Completed)
                return new LlmRunResult<T>(run.Outcome, default, run.Raw, run.Error);

            if (!parser(run.Raw, out var value, out var error))
            {
                var reason = $"{error} Response: {run.Raw[..Math.Min(200, run.Raw.Length)]}";
                logger.LogWarning("LLM run '{Label}' parse failed: {Reason}", request.Label, reason);
                broadcaster.Publish(new StreamFailed(reason));
                return new LlmRunResult<T>(LlmRunOutcome.ParseFailed, value, run.Raw, reason);
            }

            return new LlmRunResult<T>(LlmRunOutcome.Completed, value, run.Raw, null);
        }

        public Task<LlmRunResult<string>> RunAsync(LlmRunRequest request, CancellationToken ct)
            => StreamAsync(request, ct);

        /// <summary>
        /// Streams the completion, publishing lifecycle events. Returns Completed with the
        /// accumulated content, or a failure outcome; genuine cancellation throws through.
        /// </summary>
        private async Task<LlmRunResult<string>> StreamAsync(LlmRunRequest request, CancellationToken ct)
        {
            var sb = new StringBuilder();
            try
            {
                logger.LogDebug("LLM run '{Label}' against {BaseUrl}", request.Label, request.Config.BaseUrl);
                broadcaster.Publish(new RequestStarted(request.Label, request.Prompt));

                var metrics = new StreamMetrics(request.Prompt);
                var sw = Stopwatch.StartNew();
                var scanner = request.Shape switch
                {
                    CompletionShape.Object => JsonCompletionScanner.ForObject(),
                    CompletionShape.Array => JsonCompletionScanner.ForArray(),
                    _ => null,
                };

                await foreach (var chunk in llm.StreamChatAsync(request.Config, request.Prompt, request.JsonSchema, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        metrics.AddOutput(c);
                        broadcaster.Publish(new ContentDelta(c));
                        if (scanner?.Append(c) == true)
                            break;
                    }
                }

                sw.Stop();
                broadcaster.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                reporter.ReportSuccess(request.Config.BaseUrl);
                var raw = sb.ToString();
                return new LlmRunResult<string>(LlmRunOutcome.Completed, raw, raw, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // A client that wrapped the cancellation in its own exception type must still not be
                // reported as a service failure — that would trip watchdog recovery and escalate the
                // attribution chain to the next config instead of stopping.
                throw new OperationCanceledException("LLM run cancelled.", ex, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM run '{Label}' failed", request.Label);
                broadcaster.Publish(new StreamFailed(ex.Message));
                var reported = reporter.ReportFailure(request.Config.BaseUrl, ex);
                return new LlmRunResult<string>(
                    reported ? LlmRunOutcome.ServiceUnavailable : LlmRunOutcome.Failed,
                    null, sb.ToString(), ex.Message);
            }
        }
    }
}
