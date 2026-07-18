using System.Net;
using System.Text;
using System.Text.Json;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Single handler behind the app's IHttpClientFactory. Routes by fake host name
/// (the base URLs seeded into app.db: http://fake-llm, http://fake-whisper, ...).
/// Any request to an unrecognized host throws — nothing escapes to the real network.
/// </summary>
public sealed class FakeAiRoutingHandler : HttpMessageHandler
{
    /// <summary>
    /// Model the seeded llama config targets. It reads <c>loaded</c> in the default model store, so the
    /// switch-and-wait gate is a no-op for every test that doesn't opt into a switch.
    /// </summary>
    public const string DefaultModel = "fake-model";

    /// <summary>Per-test hook: given the LLM prompt, return the assistant reply content.</summary>
    public Func<string, string> LlmReply { get; set; } =
        p => FakeAiResponses.AttributionReply(p, "Narrator");

    /// <summary>
    /// Per-test llama model state driving <c>GET /v1/models</c> status and autoload semantics. Default:
    /// the seeded model reads <c>loaded</c> (no switch). A switch test swaps in
    /// <see cref="FakeLlmModelStore.Switching"/> so the target starts unloaded and loads over polls.
    /// </summary>
    public FakeLlmModelStore LlmModels { get; set; } = FakeLlmModelStore.AllLoaded(DefaultModel);

    /// <summary>Text of the last /api/stream TTS request; echoed back by fake-whisper.</summary>
    private volatile string _lastTtsText = "";

    public List<string> LlmPromptsSeen { get; } = [];

    /// <summary>
    /// Restores per-test defaults. The handler is shared across the collection, so anything a
    /// test sets (LlmReply) or the pipeline records (_lastTtsText, prompts) would otherwise
    /// leak into the next test.
    /// </summary>
    public void Reset()
    {
        LlmReply = p => FakeAiResponses.AttributionReply(p, "Narrator");
        LlmModels = FakeLlmModelStore.AllLoaded(DefaultModel);
        _lastTtsText = "";
        lock (LlmPromptsSeen) LlmPromptsSeen.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath;

        return host switch
        {
            "fake-llm" => await HandleLlmAsync(request, path, ct),
            "fake-whisper" => Json(FakeAiResponses.WhisperVerboseJson(
                _lastTtsText.Length > 0 ? _lastTtsText : "transcript")),
            "fake-similarity" => Json("""{"similarity": 1.0}"""),
            "fake-tts" => await HandleTtsAsync(request, path, ct),
            "fake-voicedesign" => await HandleTtsAsync(request, path, ct),
            _ => throw new InvalidOperationException(
                $"FakeAiRoutingHandler: unexpected request to {request.RequestUri} — a real network call escaped the fakes."),
        };
    }

    private async Task<HttpResponseMessage> HandleLlmAsync(
        HttpRequestMessage request, string path, CancellationToken ct)
    {
        if (path.EndsWith("v1/models", StringComparison.Ordinal))
            return Json(LlmModels.RenderJson());

        if (path.EndsWith("v1/chat/completions", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            // Naming a model kicks off its autoload in the model store (the real fork's --models-max 1
            // behaviour); the switch-and-wait gate's max_tokens=1 trigger and the real request both land here.
            LlmModels.NoteRequest(ExtractModel(body));
            var prompt = ExtractPrompt(body);
            lock (LlmPromptsSeen) LlmPromptsSeen.Add(prompt);
            var sse = FakeAiResponses.OpenAiSse(LlmReply(prompt));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
            return response;
        }

        throw new InvalidOperationException($"fake-llm: unexpected path {path}");
    }

    private async Task<HttpResponseMessage> HandleTtsAsync(
        HttpRequestMessage request, string path, CancellationToken ct)
    {
        if (path.EndsWith("/upload-audio", StringComparison.Ordinal))
            return Json("""{"file_id": "fake-file-id"}""");

        if (path.EndsWith("/api/stream", StringComparison.Ordinal))
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var t) && t.GetString() is { } text)
                _lastTtsText = text;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakeAiResponses.VoxCpm2StreamFrames()),
            };
        }

        // Generic TTS endpoint (qwen3 etc.): return a silent WAV.
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(FakeAiResponses.SilentWav()),
        };
    }

    private static string? ExtractModel(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
    }

    private static string ExtractPrompt(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        if (doc.RootElement.TryGetProperty("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
                if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString() ?? "";
        }
        if (doc.RootElement.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
            return p.GetString() ?? "";
        return requestBody;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain"),
    };
}
