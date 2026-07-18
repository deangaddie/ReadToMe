using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services.Health;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Serialises model switches on a switchable llama endpoint (the <c>--models-max 1</c> autoload
    /// fork). Before a request runs, detects whether the target model is loaded via
    /// <c>GET /v1/models</c>; if not, triggers an autoload (a <c>max_tokens=1</c> chat completion)
    /// out-of-band and polls until the model reads <c>loaded</c>, so the real request never times out
    /// mid-load. Singleton: the per-endpoint locks must outlive the scoped <see cref="OpenAiLlmClient"/>.
    /// It owns its own HTTP and never calls <see cref="ILlmClient"/> (that would recurse).
    /// </summary>
    public sealed class ModelLoadGate : IModelLoadGate
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ModelLoadGate> _logger;
        private readonly AiWatchdogOptions _options;

        // One lock per endpoint (keyed by BaseUrl). Singleton lifetime keeps this state alive across
        // the scoped clients that call the gate.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public ModelLoadGate(
            IHttpClientFactory httpClientFactory,
            ILogger<ModelLoadGate> logger,
            IOptions<AiWatchdogOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _options = options.Value;
        }

        public async Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct)
        {
            // Non-switchable endpoints (remote OpenAI-compatible servers) never switch models.
            if (!config.SupportsModelSwitch)
                return;

            // Hot same-model path: detect lock-free every time (no cache) so parallel callers naming the
            // already-loaded model never serialise on the lock.
            if (await IsTargetLoadedAsync(config, ct))
                return;

            var gate = _locks.GetOrAdd(config.BaseUrl, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Re-check under the lock: a concurrent caller may have just loaded it (coalescing).
                if (await IsTargetLoadedAsync(config, ct))
                    return;

                await SwitchAndPollAsync(config, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task SwitchAndPollAsync(LlmServerConfig config, CancellationToken ct)
        {
            var budget = TimeSpan.FromSeconds(Math.Max(0, _options.ModelLoadBudgetSeconds));
            var pollInterval = TimeSpan.FromSeconds(Math.Max(0, _options.ModelLoadPollIntervalSeconds));
            var model = config.Model ?? string.Empty;

            _logger.LogInformation(
                "Switching {BaseUrl} to model {Model}; waiting up to {Budget}s.",
                config.BaseUrl, model, budget.TotalSeconds);

            var stopwatch = Stopwatch.StartNew();

            // The trigger's HttpClient timeout must exceed the budget: an autoload request blocks until
            // the load finishes, and aborting it mid-load would defeat the whole switch.
            var triggerHttp = CreateClient(config, budget + TimeSpan.FromSeconds(30));
            var url = OpenAiStreamParser.Combine(config.BaseUrl, "v1/chat/completions");
            var body = BuildTriggerBody(config);

            // Detached autoload trigger, CT linked to the caller. We do NOT await its slow completion
            // (a valid switch blocks for ~a minute); we only observe it to surface an immediate hard
            // failure (fast non-2xx / connection error) and to cancel it once the model is loaded.
            using var triggerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var triggerTask = TriggerAutoloadAsync(triggerHttp, url, body, config.BaseUrl, triggerCts.Token);

            try
            {
                while (true)
                {
                    // Surface an immediate hard failure (e.g. a fast 400 for an unknown model, or a
                    // connection error). A successful/normal completion just means the load returned —
                    // fall through and let the poll confirm it.
                    if (triggerTask.IsFaulted)
                        await triggerTask;

                    if (await IsTargetLoadedAsync(config, ct))
                        return;

                    if (stopwatch.Elapsed >= budget)
                        throw new ModelStillLoadingException(config.BaseUrl, model, stopwatch.Elapsed, budget);

                    await Task.Delay(pollInterval, ct);
                }
            }
            finally
            {
                // Loaded (or failing out): stop the trigger and observe any exception so a fire-and-forget
                // fault never surfaces as unobserved.
                triggerCts.Cancel();
                _ = triggerTask.ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task TriggerAutoloadAsync(
            HttpClient http, string url, string body, string baseUrl, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                response = await http.SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // We cancelled the trigger because the model finished loading. Nothing to report.
                throw;
            }
            catch (Exception ex)
            {
                throw new LlmProviderException($"Failed to connect to LLM provider at {baseUrl}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new LlmProviderException(
                        $"LLM provider returned error ({response.StatusCode}): {error}", null!);
                }
            }
        }

        private async Task<bool> IsTargetLoadedAsync(LlmServerConfig config, CancellationToken ct)
        {
            var http = CreateClient(config);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(OpenAiStreamParser.Combine(config.BaseUrl, "v1/models"), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LlmProviderException($"Failed to connect to LLM provider at {config.BaseUrl}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new LlmProviderException(
                        $"LLM provider returned error ({response.StatusCode}): {error}", null!);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                return TargetStatusIsLoaded(doc.RootElement, config.Model);
            }
        }

        /// <summary>
        /// True only when the item in <c>data[]</c> whose <c>id</c> equals <paramref name="model"/>
        /// reports <c>status.value == "loaded"</c>. Any other state (unloaded/loading/absent) is false.
        /// </summary>
        private static bool TargetStatusIsLoaded(JsonElement root, string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return false;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String ||
                    !string.Equals(id.GetString(), model, StringComparison.Ordinal))
                    continue;

                if (item.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return string.Equals(value.GetString(), "loaded", StringComparison.Ordinal);

                return false;
            }

            return false;
        }

        // A minimal autoload request: names the target model with max_tokens=1 so the fork evicts the
        // current model and loads the target with negligible generation cost.
        private static string BuildTriggerBody(LlmServerConfig config)
        {
            var probe = new LlmServerConfig { Model = config.Model, MaxTokens = 1 };
            return OpenAiRequestBuilder.BuildChatBody(probe, "ping", stream: false);
        }

        private HttpClient CreateClient(LlmServerConfig config, TimeSpan? timeout = null)
        {
            var http = _httpClientFactory.CreateClient();
            if (timeout is { } t)
                http.Timeout = t;
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            return http;
        }
    }
}
