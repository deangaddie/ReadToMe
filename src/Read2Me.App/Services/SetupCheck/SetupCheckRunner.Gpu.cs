using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Services.Preflight;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Full check's GPU services, one at a time after the CPU ones: audiocpp's models (paragraph
    /// TTS, then voice design), then llama's, one 1-token completion per distinct model. Each visit
    /// to a GPU container first goes through the Preflight conflict sweep, which stops the rival GPU
    /// container; putting back starts it again, and llama reloads the model it had.
    /// </summary>
    public sealed partial class SetupCheckRunner
    {
        /// <summary>audio.cpp and llama load a model on the first request naming it: 9–31 s.</summary>
        private const int ModelSwitchSeconds = 30;

        private const string Llama = "llama";
        private const string TtsSentence = "It is a truth universally acknowledged.";
        private const string DesignPrompt = "A clear, warm adult narrator reading a novel aloud at a calm, even pace.";

        /// <summary>One distinct LLM model the run exercises, and which rows it answers for.</summary>
        private sealed class LlmTarget(LlmServerConfig config, string? baseUrl, DockerAiService? service)
        {
            public LlmServerConfig Config { get; } = config;
            public string? BaseUrl { get; } = baseUrl;
            public DockerAiService? Service { get; } = service;
            public string? Model => ServiceConfigModelIds.For(Config);
            public string Label => Model ?? Config.Name;

            /// <summary>The active LLM config names it.</summary>
            public bool Active { get; set; }

            /// <summary>An attribution chain entry names it.</summary>
            public bool InChain { get; set; }

            /// <summary>What its completion found, once its step has run.</summary>
            public CheckAnswer? Answer { get; set; }
        }

        private IEnumerable<FullStep> GpuSteps(FullContext context)
        {
            if (context.ParagraphTts is { } tts)
                yield return new FullStep(
                    SetupCheckLane.Gpu,
                    $"Speaking a sentence with {ServiceConfigModelIds.For(tts) ?? tts.Name} on {Where(context.TtsService, ServiceConfigBaseUrls.For(tts))}",
                    RowIds(SetupCheckIds.ParagraphTts, context.TtsService), Driven(context.TtsService),
                    ModelSwitchSeconds + 15, () => FullParagraphTtsAsync(context));
            if (context.VoiceDesign is { } design)
                yield return new FullStep(
                    SetupCheckLane.Gpu,
                    $"Designing a voice with {ServiceConfigModelIds.For(design) ?? design.Name} on {Where(context.DesignService, ServiceConfigBaseUrls.For(design))}",
                    RowIds(SetupCheckIds.VoiceDesign, context.DesignService), Driven(context.DesignService),
                    ModelSwitchSeconds + 15, () => FullVoiceDesignAsync(context));
            foreach (var target in context.Llm)
                yield return new FullStep(
                    SetupCheckLane.Gpu,
                    $"Loading {target.Label} on {Where(target.Service, target.BaseUrl)} for a 1-token completion",
                    LlmRowIds(target), Driven(target.Service),
                    ModelSwitchSeconds + 5, () => FullLlmAsync(context, target));
        }

        /// <summary>A GPU visit may sweep any other GPU container, so the watchdog holds off them all.</summary>
        private IReadOnlyList<string> Driven(DockerAiService? service) => service switch
        {
            null => [],
            { UsesGpu: true } => [.. aiServices.Registry.All.Where(s => s.UsesGpu).Select(s => s.Name)],
            _ => [service.Name],
        };

        private static IReadOnlyList<string> LlmRowIds(LlmTarget target)
        {
            var ids = new List<string>();
            if (target.Active)
                ids.Add(SetupCheckIds.Llm);
            if (target.InChain)
                ids.Add(SetupCheckIds.AttributionChain);
            if (target.Service is { } service)
                ids.Add(SetupCheckIds.Container(service.Name));
            return ids;
        }

        // ── Paragraph TTS and voice design ───────────────────────────────────

        private async Task<IReadOnlyList<SetupCheckRow>> FullParagraphTtsAsync(FullContext context)
        {
            var config = context.ParagraphTts!;
            var row = context.Row(SetupCheckIds.ParagraphTts, SetupCheckGroup.AiConfigs, "Paragraph TTS") with
            {
                Tier = SetupCheckTier.Full,
                Expected = "A short sentence spoken with the sample clip as the reference voice, which ffprobe reads a duration from",
            };
            return await RequestCheckAsync(context, context.TtsService, row, AiServicesCheck.TtsSettings, SpeechUrl(ServiceConfigBaseUrls.For(config)), async () =>
            {
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var client = scope.ServiceProvider.GetKeyedService<IParagraphTtsClient>(config.Type)
                    ?? throw new NotSupportedException($"No paragraph TTS client for {config.Type}");
                await using var clip = SampleClip.OpenWav();
                await using var audio = await client.GenerateAsync(TtsSentence, null, clip, config, null, SampleClip.Text);
                return await AudioAnswerAsync(audio, "Spoke", AiServicesCheck.TtsSettings);
            }, ServiceConfigModelIds.For(config));
        }

        private async Task<IReadOnlyList<SetupCheckRow>> FullVoiceDesignAsync(FullContext context)
        {
            var config = context.VoiceDesign!;
            var row = context.Row(SetupCheckIds.VoiceDesign, SetupCheckGroup.AiConfigs, "Voice design") with
            {
                Tier = SetupCheckTier.Full,
                Expected = "A voice designed from a fixed prompt, speaking the sample text, which ffprobe reads a duration from",
            };
            return await RequestCheckAsync(context, context.DesignService, row, AiServicesCheck.VoiceDesignSettings, SpeechUrl(ServiceConfigBaseUrls.For(config)), async () =>
            {
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var client = scope.ServiceProvider.GetKeyedService<IVoiceDesignClient>(config.Type)
                    ?? throw new NotSupportedException($"No voice design client for {config.Type}");
                await using var audio = await client.DesignVoiceAsync(config, DesignPrompt, SampleClip.Text, null);
                return await AudioAnswerAsync(audio, "Designed", AiServicesCheck.VoiceDesignSettings);
            }, ServiceConfigModelIds.For(config));
        }

        private static string SpeechUrl(string? baseUrl) => $"{baseUrl?.TrimEnd('/')}/v1/audio/speech";

        /// <summary>The audio passes when the live ffmpeg's ffprobe reads a duration above zero from it.</summary>
        private async Task<CheckAnswer> AudioAnswerAsync(Stream audio, string verb, SetupSettingsLink settings)
        {
            var paths = await ReadFfmpegPathsAsync(CancellationToken.None);
            var measured = await full.Encode.MeasureAudioAsync(audio, paths.Live, CancellationToken.None);
            return measured switch
            {
                { Duration: { } d } when d > TimeSpan.Zero =>
                    new CheckAnswer(true, $"{verb} {d.TotalSeconds:0.0} s of audio"),
                { Duration: not null } => new CheckAnswer(false, $"{verb} audio that ffprobe reads as 0 s long",
                    new SetupFixHint($"It answered with no sound: check the config's model in {settings.Label}.", settings)),
                _ => new CheckAnswer(false, $"ffprobe couldn't read the audio: {measured.Error}", FfmpegFix),
            };
        }

        // ── LLM ──────────────────────────────────────────────────────────────

        private async Task<IReadOnlyList<SetupCheckRow>> FullLlmAsync(FullContext context, LlmTarget target)
        {
            var url = $"{target.BaseUrl?.TrimEnd('/')}/v1/chat/completions";
            var (answer, container) = await SendCheckAsync(context, target.Service, AiServicesCheck.LlmSettings, url, async () =>
            {
                var content = await CompleteOneTokenAsync(target.Config);
                return content.Length > 0
                    ? new CheckAnswer(true, $"answered \"{content}\"")
                    : new CheckAnswer(false, "answered 200 with no content",
                        new SetupFixHint($"Check the model's chat template, or correct the model in {AiServicesCheck.LlmSettings.Label}.", AiServicesCheck.LlmSettings));
            }, target.Model);
            target.Answer = answer;

            var rows = new List<SetupCheckRow>();
            if (target.Active)
                rows.Add(LlmRow(context, SetupCheckIds.Llm, "LLM", context.Llm.Where(t => t.Active)));
            if (target.InChain)
                rows.Add(LlmRow(context, SetupCheckIds.AttributionChain, "Attribution chain", context.Llm.Where(t => t.InChain)));
            if (container is not null)
                rows.Add(container);
            return rows;
        }

        /// <summary>A row a model answers for, judged on every one of its models answered so far: one failure fails it.</summary>
        private static SetupCheckRow LlmRow(FullContext context, string id, string name, IEnumerable<LlmTarget> targets)
        {
            var all = targets.ToList();
            var answered = all.Where(t => t.Answer is not null).ToList();
            var failed = answered.FirstOrDefault(t => !t.Answer!.Passed);
            return context.Row(id, SetupCheckGroup.AiConfigs, name) with
            {
                Tier = SetupCheckTier.Full,
                Expected = $"A 1-token chat completion with thinking off answers with text from {string.Join(", ", all.Select(t => t.Label))}",
                Result = failed is null ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                Found = string.Join("; ", answered.Select(t => $"{t.Label} {t.Answer!.Found}")),
                Fix = failed?.Answer!.Fix ?? new SetupFixHint($"Change it in {AiServicesCheck.LlmSettings.Label}.", AiServicesCheck.LlmSettings),
            };
        }

        /// <summary>
        /// One non-streamed chat completion, max_tokens 1 and thinking off, naming the config's model,
        /// which llama loads on the way. Answers the content; throws when it doesn't answer 200.
        /// </summary>
        private async Task<string> CompleteOneTokenAsync(LlmServerConfig config)
        {
            var body = OpenAiRequestBuilder.BuildChatBody(
                config, "Reply with one word.", stream: false, disableThinking: true, overrides: new LlmRunOverrides(MaxTokens: 1));
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiStreamParser.Combine(config.BaseUrl, "v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            using var response = await aiServices.Http.CreateClient().SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {text}");

            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()!.Trim()
                : "";
        }

        /// <summary>The model llama reports <c>loaded</c> on <c>GET /v1/models</c>; null when none is or it didn't answer.</summary>
        private async Task<string?> LoadedModelAsync(DockerAiService service)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await aiServices.Http.CreateClient()
                    .GetAsync($"{service.BaseUrl.TrimEnd('/')}/v1/models", timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                        && status.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                        && value.GetString() == "loaded"
                        && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ── Sweep and missing models ─────────────────────────────────────────

        /// <summary>The Preflight conflict sweep before a GPU visit: every other GPU container running is stopped.</summary>
        private async Task SweepAsync(FullContext context, DockerAiService service)
        {
            foreach (var rival in await GpuConflictSweep.FindAsync(aiServices.Control, aiServices.Registry, [service], CancellationToken.None))
            {
                var use = await TrackAsync(context, rival, running: true);
                if ((await aiServices.Control.ShutdownAsync(rival, CancellationToken.None)).Succeeded)
                    use.Running = false;
            }
        }

        [GeneratedRegex(@"/models/[^\s""',;}\]]+")]
        private static partial Regex ModelPath();

        /// <summary>
        /// audio.cpp answers 500 "model path does not exist" and llama "failed to load" when the GGUF
        /// isn't where the model entry says: the hint names the path and where the mount comes from.
        /// Null when the error is something else.
        /// </summary>
        private static SetupFixHint? MissingModelFix(DockerAiService? service, string? model, string error)
        {
            if (!error.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                && !error.Contains("failed to load", StringComparison.OrdinalIgnoreCase))
                return null;

            var path = ModelPath().Match(error) is { Success: true } match ? match.Value : null;
            var file = path ?? (service?.Name == Llama
                ? $"the GGUF the {model} preset names in Infra/llama/config/models.ini"
                : $"the GGUF the {model} entry names in Infra/audiocpp/server.json");
            var restart = service is null ? "then restart the service where it runs" : $"then run docker compose up -d {service.Name} from Infra/";
            return new SetupFixHint(
                $"The model file is missing: {file}. Put it in the folder GGUF_MODELS_DIR names in Infra/.env (mounted at /models), {restart}.",
                Copy: path ?? "Infra/.env");
        }
    }
}
