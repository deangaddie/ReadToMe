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
            if (context.Configs.ParagraphTts is { Config: var tts, Service: var ttsService })
                yield return new FullStep(
                    SetupCheckLane.Gpu,
                    $"Speaking a sentence with {ServiceConfigModelIds.For(tts) ?? tts.Name} on {Where(ttsService, ServiceConfigBaseUrls.For(tts))}",
                    RowIds(SetupCheckIds.ParagraphTts, ttsService), Driven(ttsService),
                    ModelSwitchSeconds + 15, () => FullParagraphTtsAsync(context));
            if (context.Configs.VoiceDesign is { Config: var design, Service: var designService })
                yield return new FullStep(
                    SetupCheckLane.Gpu,
                    $"Designing a voice with {ServiceConfigModelIds.For(design) ?? design.Name} on {Where(designService, ServiceConfigBaseUrls.For(design))}",
                    RowIds(SetupCheckIds.VoiceDesign, designService), Driven(designService),
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
            var (config, ttsService) = context.Configs.ParagraphTts!;
            var row = context.Row(SetupCheckIds.ParagraphTts, SetupCheckGroup.AiConfigs, "Paragraph TTS") with
            {
                Tier = SetupCheckTier.Full,
                Expected = "A short sentence spoken with the sample clip as the reference voice, which ffprobe reads a duration from",
            };
            return await RequestCheckAsync(context, ttsService, row, SetupSettingsLink.Tts, SpeechUrl(ServiceConfigBaseUrls.For(config)), async () =>
            {
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var client = scope.ServiceProvider.GetKeyedService<IParagraphTtsClient>(config.Type)
                    ?? throw new NotSupportedException($"No paragraph TTS client for {config.Type}");
                await using var clip = SampleClip.OpenWav();
                await using var audio = await client.GenerateAsync(TtsSentence, null, clip, config, null, SampleClip.Text);
                return await AudioAnswerAsync(audio, "Spoke", SetupSettingsLink.Tts);
            }, ServiceConfigModelIds.For(config));
        }

        private async Task<IReadOnlyList<SetupCheckRow>> FullVoiceDesignAsync(FullContext context)
        {
            var (config, designService) = context.Configs.VoiceDesign!;
            var row = context.Row(SetupCheckIds.VoiceDesign, SetupCheckGroup.AiConfigs, "Voice design") with
            {
                Tier = SetupCheckTier.Full,
                Expected = "A voice designed from a fixed prompt, speaking the sample text, which ffprobe reads a duration from",
            };
            return await RequestCheckAsync(context, designService, row, SetupSettingsLink.VoiceDesign, SpeechUrl(ServiceConfigBaseUrls.For(config)), async () =>
            {
                await using var scope = aiServices.Scopes.CreateAsyncScope();
                var client = scope.ServiceProvider.GetKeyedService<IVoiceDesignClient>(config.Type)
                    ?? throw new NotSupportedException($"No voice design client for {config.Type}");
                await using var audio = await client.DesignVoiceAsync(config, DesignPrompt, SampleClip.Text, null);
                return await AudioAnswerAsync(audio, "Designed", SetupSettingsLink.VoiceDesign);
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
            var (answer, container) = await SendCheckAsync(context, target.Service, SetupSettingsLink.Llm, url, async () =>
            {
                var content = await CompleteOneTokenAsync(target.Config);
                return content.Length > 0
                    ? new CheckAnswer(true, $"answered \"{content}\"")
                    : new CheckAnswer(false, "answered 200 with no content",
                        new SetupFixHint($"Check the model's chat template, or correct the model in {SetupSettingsLink.Llm.Label}.", SetupSettingsLink.Llm));
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
                Fix = failed?.Answer!.Fix ?? new SetupFixHint($"Change it in {SetupSettingsLink.Llm.Label}.", SetupSettingsLink.Llm),
            };
        }

        /// <summary>
        /// One non-streamed chat completion, max_tokens 1 and thinking off, naming the config's model,
        /// which llama loads on the way. Answers the content; throws when it doesn't answer 200, with
        /// the status on the exception when the server answered. A probe, so it goes straight to the
        /// endpoint rather than through <see cref="ILlmCompletionRunner"/>: that would put it in the live
        /// stream, report health to a watchdog the run holds off, and wait out a load the check must time.
        /// </summary>
        private async Task<string> CompleteOneTokenAsync(LlmServerConfig config)
        {
            var body = LlamaModelStatus.OneTokenRequestBody(config.Model, disableThinking: true);
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiStreamParser.Combine(config.BaseUrl, "v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            using var response = await aiServices.Http.CreateClient().SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {text}", null, response.StatusCode);

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
                return LlamaModelStatus.LoadedModel(doc.RootElement);
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
                {
                    use.Running = false;
                    use.Swept = true;
                }
            }
        }

        [GeneratedRegex(@"/models/[^\s""',;}\]]+")]
        private static partial Regex ModelPath();

        private const string GgufFolder = "the folder GGUF_MODELS_DIR names in Infra/.env (mounted at /models)";

        /// <summary>
        /// audio.cpp answers 500 "model path does not exist" with the path: the hint names it and where
        /// the mount comes from. llama's "failed to load" names no path and may be a missing GGUF, too
        /// little memory or a bad preset: the hint says where the GGUF should be and to read the logs.
        /// Null when the error is something else.
        /// </summary>
        private static SetupFixHint? ModelLoadFix(DockerAiService? service, string? model, string error)
        {
            var restart = service is null ? "then restart the service where it runs" : $"then run docker compose up -d {service.Name} from Infra/";
            if (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                var path = ModelPath().Match(error) is { Success: true } match ? match.Value : null;
                var file = path ?? $"the GGUF the {model} entry names in Infra/audiocpp/server.json";
                return new SetupFixHint($"The model file is missing: {file}. Put it in {GgufFolder}, {restart}.",
                    Copy: path ?? "Infra/.env");
            }
            if (!error.Contains("failed to load", StringComparison.OrdinalIgnoreCase))
                return null;

            var where = service?.Name == Llama ? "the GGUF its preset names in Infra/llama/config/models.ini" : "its GGUF";
            var logs = service is null ? "the service's logs" : $"docker logs -f {service.ContainerName}";
            return new SetupFixHint(
                $"{service?.Name ?? "The service"} couldn't load {model}: check that {where} is in {GgufFolder}, then read why with {logs}.",
                Copy: service is null ? "Infra/.env" : $"docker logs -f {service.ContainerName}");
        }
    }
}
