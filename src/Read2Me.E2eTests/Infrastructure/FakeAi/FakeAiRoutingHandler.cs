using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// Per-test hook: how long each LLM completion takes before answering, so a browser test can
    /// watch the queue while it is still busy. Zero by default.
    /// </summary>
    public TimeSpan LlmDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Text the last TTS request spoke; echoed back by fake-whisper.</summary>
    private volatile string _lastTtsText = "";

    public List<string> LlmPromptsSeen { get; } = [];

    /// <summary>JSON bodies of every fake-audiocpp <c>POST /v1/audio/speech</c>, oldest first.</summary>
    public List<JsonObject> AudioCppSpeechBodies { get; } = [];

    /// <summary>The model fake-audiocpp reports <c>loaded</c>; a speech request loads the model it names.</summary>
    private volatile string? _audioCppLoaded;

    /// <summary>
    /// Restores per-test defaults. The handler is shared across the collection, so anything a
    /// test sets (LlmReply) or the pipeline records (_lastTtsText, prompts) would otherwise
    /// leak into the next test.
    /// </summary>
    public void Reset()
    {
        LlmReply = p => FakeAiResponses.AttributionReply(p, "Narrator");
        LlmDelay = TimeSpan.Zero;
        LlmModels = FakeLlmModelStore.AllLoaded(DefaultModel);
        _lastTtsText = "";
        lock (LlmPromptsSeen) LlmPromptsSeen.Clear();
        lock (AudioCppSpeechBodies) AudioCppSpeechBodies.Clear();
        _audioCppLoaded = null;
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
            "fake-tts" => HandleTts(),
            "fake-audiocpp" => await HandleAudioCppAsync(request, path, ct),
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
            // Naming a model kicks off its autoload in the model store (the real llama.cpp router's --models-max 1
            // behaviour); the switch-and-wait gate's max_tokens=1 trigger and the real request both land here.
            LlmModels.NoteRequest(ExtractModel(body));
            var prompt = ExtractPrompt(body);
            lock (LlmPromptsSeen) LlmPromptsSeen.Add(prompt);
            if (LlmDelay > TimeSpan.Zero) await Task.Delay(LlmDelay, ct);
            var sse = FakeAiResponses.OpenAiSse(LlmReply(prompt));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
            return response;
        }

        throw new InvalidOperationException($"fake-llm: unexpected path {path}");
    }

    /// <summary>The native TTS service not yet on audio.cpp (ChatterboxTurbo): a silent WAV.</summary>
    private static HttpResponseMessage HandleTts() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(FakeAiResponses.SilentWav()),
    };

    /// <summary>
    /// The audio.cpp TTS runtime: <c>GET /v1/models</c> and <c>POST /v1/audio/speech</c> (JSON in,
    /// WAV out). The spoken input feeds fake-whisper, as the other TTS fakes' text does.
    /// </summary>
    private async Task<HttpResponseMessage> HandleAudioCppAsync(
        HttpRequestMessage request, string path, CancellationToken ct)
    {
        if (path == "/v1/models")
        {
            var models = new[] { "breeze-q8", "breeze-design", "voxcpm2", "chatterbox", "qwen3-base", "qwen3-design" }
                .Select(id => new { id, loaded = id == _audioCppLoaded });
            return Json(JsonSerializer.Serialize(new { @object = "list", data = models }));
        }

        if (path == "/v1/audio/speech")
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            lock (AudioCppSpeechBodies) AudioCppSpeechBodies.Add(body);
            _lastTtsText = SpokenText(body);
            _audioCppLoaded = body["model"]?.GetValue<string>();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakeAiResponses.SilentWav()),
            };
        }

        throw new InvalidOperationException($"fake-audiocpp: unexpected path {path}");
    }

    /// <summary>
    /// What a real model would say: VoxCPM2 reads a leading <c>(control)</c> as direction, not
    /// words, so fake-whisper must not hear it either.
    /// </summary>
    private static string SpokenText(JsonObject body)
    {
        var input = body["input"]?.GetValue<string>() ?? "";
        if (body["model"]?.GetValue<string>() == "voxcpm2" && input.StartsWith('(')
            && input.IndexOf(')') is var close and > 0)
            return input[(close + 1)..];
        return input;
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
