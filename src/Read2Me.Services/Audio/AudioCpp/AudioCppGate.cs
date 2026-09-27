using System.Collections.Concurrent;
using System.Text.Json;
using Read2Me.Services.Events;

namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// Sends requests to an audio.cpp endpoint one at a time. audio.cpp keeps one model resident and
    /// answers a request for a different model with an immediate 503 while it is generating; the
    /// audio queue is serial, but voice design runs beside it, so the app serialises per endpoint.
    /// </summary>
    public interface IAudioCppGate
    {
        /// <summary>
        /// Under the endpoint's lock: checks <c>GET /v1/models</c>, publishes
        /// <see cref="TtsModelLoading"/> when the server lists <paramref name="modelId"/> but has not loaded it, then
        /// runs <paramref name="send"/>. There is no separate load call — audio.cpp loads the model
        /// as part of the request itself.
        /// </summary>
        Task<T> RunAsync<T>(string baseUrl, string modelId, Func<CancellationToken, Task<T>> send, CancellationToken ct);
    }

    /// <summary>
    /// Singleton: the per-endpoint locks must outlive the scoped clients that call it — the same
    /// pattern as the llama <c>ModelLoadGate</c>.
    /// </summary>
    public sealed class AudioCppGate(
        IHttpClientFactory httpClientFactory,
        EventBroadcaster<AudioGenEvent> broadcaster) : IAudioCppGate
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public async Task<T> RunAsync<T>(
            string baseUrl, string modelId, Func<CancellationToken, Task<T>> send, CancellationToken ct)
        {
            var gate = _locks.GetOrAdd(baseUrl, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (await IsLoadedAsync(baseUrl, modelId, ct) == false)
                    broadcaster.Publish(new TtsModelLoading(modelId));

                return await send(ct);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// The <c>loaded</c> flag of the <c>data[]</c> entry whose <c>id</c> is the model; null when the
        /// server doesn't list it — a mistyped ModelId is not "loading", audio.cpp will reject it.
        /// </summary>
        private async Task<bool?> IsLoadedAsync(string baseUrl, string modelId, CancellationToken ct)
        {
            var http = httpClientFactory.CreateClient(AudioCppClient.HttpClientName);
            using var response = await http.GetAsync(baseUrl + "/v1/models", ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() == modelId)
                    return model.TryGetProperty("loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True;
            }

            return null;
        }
    }
}
