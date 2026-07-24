using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Exceptions;
using Read2Me.Services.Events;
using Read2Me.Services.Health;

namespace Read2Me.Services.Llm
{
    public sealed class LlmCompletionRunner(
        ILlmClient llm,
        IAiServiceReporter reporter,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        EventBroadcaster<LlmTimingsSample> samples,
        ILogger<LlmCompletionRunner> logger) : ILlmCompletionRunner
    {
        /// <summary>
        /// The arrival stamp for a chunk, on a process-wide monotonic origin. The origin itself is
        /// meaningless — consumers only difference these — but it must span the whole run: a
        /// per-request stopwatch restarted at every request, which the accumulator does not care
        /// about but which would collide a run's ring buckets across its requests.
        /// </summary>
        /// <remarks>
        /// This exists so that every consumer of a reading can stay clock-free. The stamp is the
        /// producer's job, and this is the producer.
        /// </remarks>
        private static TimeSpan Arrival() => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());

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

            // Nothing timed here is displayed — the stamps only let the accumulator slice its
            // window, and the aggregator bucket its ring, without either reading a clock itself.
            // Every displayed figure comes from the server's own timings.
            //
            // Declared outside the try so the abort paths below can still read it. An aborted
            // stream sends no final chunk, so this is the only surviving record of what a
            // watchdog-killed or user-cancelled request actually generated — real work, really
            // measured, and dropping it would make a run total less accurate, not more (ADR 0003).
            var timings = new TimingsAccumulator();
            try
            {
                logger.LogDebug("LLM run '{Label}' against {BaseUrl}", request.Label, request.Config.BaseUrl);
                broadcaster.Publish(new RequestStarted(request.Label, request.Prompt,
                    request.Config.Id, request.Config.Name));

                LlmUsage? usage = null;
                var scanner = request.Shape switch
                {
                    CompletionShape.Object => JsonCompletionScanner.ForObject(),
                    CompletionShape.Array => JsonCompletionScanner.ForArray(),
                    _ => null,
                };

                await foreach (var chunk in llm.StreamChatAsync(
                    request.Config, request.Prompt, request.JsonSchema, request.DisableThinking,
                    request.Overrides, ct))
                {
                    // One stamp, both consumers: the accumulator's window and the aggregator's ring
                    // must agree about when this chunk landed.
                    var arrival = Arrival();
                    timings.Add(chunk.Timings, arrival);
                    usage = chunk.Usage ?? usage;

                    // Published on its own bus, not the LlmStreamEvent family: one of these rides
                    // every token, and every LlmStreamEvent subscriber repaints or journals on
                    // whatever it receives. See LlmTimingsSample.
                    if (chunk.Timings is { } sample)
                        samples.Publish(new LlmTimingsSample(sample, arrival));

                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        broadcaster.Publish(new ContentDelta(c));
                        if (scanner?.Append(c) == true)
                            break;
                    }
                }

                broadcaster.Publish(new StreamCompleted(usage?.PromptTokens, timings.TokensOut,
                    timings.GenerationMs, timings.Rate));

                reporter.ReportSuccess(request.Config.BaseUrl);
                var raw = sb.ToString();
                return new LlmRunResult<string>(LlmRunOutcome.Completed, raw, raw, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                PublishAborted(timings);
                throw;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // A client that wrapped the cancellation in its own exception type must still not be
                // reported as a service failure — that would trip watchdog recovery and escalate the
                // attribution chain to the next config instead of stopping.
                PublishAborted(timings);
                throw new OperationCanceledException("LLM run cancelled.", ex, ct);
            }
            catch (ModelStillLoadingException ex)
            {
                // The model is still loading on a switchable endpoint (the switch-and-wait gate gave
                // up within its budget while the server stayed responsive). This is "provider busy",
                // not "provider dead": reporting a failure here would trip watchdog recovery (a
                // container restart mid-load) and escalate the attribution chain to the next config
                // (evicting the very model we are waiting for). So we skip reporter.ReportFailure and
                // publish no StreamFailed — like the cancel paths above — but still hand the abort
                // reading to the aggregator for timing bookkeeping. The queue requeues with backoff.
                logger.LogInformation(
                    "LLM run '{Label}' deferred — model still loading: {Message}", request.Label, ex.Message);
                PublishAborted(timings);
                return new LlmRunResult<string>(LlmRunOutcome.ModelLoading, null, sb.ToString(), ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM run '{Label}' failed", request.Label);
                // Measurement first, then outcome. StreamAborted carries what the request managed to
                // generate; StreamFailed carries the error the user sees. Two events because the
                // cancel paths above need the first without the second.
                PublishAborted(timings);
                broadcaster.Publish(new StreamFailed(ex.Message));
                var reported = reporter.ReportFailure(request.Config.BaseUrl, ex);
                return new LlmRunResult<string>(
                    reported ? LlmRunOutcome.ServiceUnavailable : LlmRunOutcome.Failed,
                    null, sb.ToString(), ex.Message);
            }
        }

        /// <summary>
        /// Hands an aborted request's last reading to the aggregator, so the work it really did
        /// still counts toward its run's total.
        /// </summary>
        /// <remarks>
        /// A request that died before its first reading publishes all-nulls: absence, never
        /// <c>0</c>. The event is still published in that case — it says "this request is over and
        /// nothing was measurable", which is what stops it zeroing a total it never contributed to,
        /// and it stays counted in its config's <c>req</c> column from its <see cref="RequestStarted"/>.
        /// </remarks>
        private void PublishAborted(TimingsAccumulator timings) =>
            broadcaster.Publish(new StreamAborted(
                timings.TokensOut, timings.GenerationMs, timings.Rate));
    }
}
