using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Services;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Quick check's AI configs and Docker services rows. It reads the active configs, each
    /// container's Managed service status and what llama and audiocpp list; it never starts, stops
    /// or switches anything.
    /// </summary>
    internal sealed class AiServicesCheck(SetupCheckServices deps)
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private const string NotManaged = "not managed — the app can't start or restart it";
        private const string NoAccuracyScore = "audio is generated without an accuracy score";
        private const string WatchdogOff = "AiWatchdog:Enabled is false: a wedged container won't be restarted";

        private static readonly SetupSettingsLink LlmSettings = new("settings/llm", "Settings → LLM");
        private static readonly SetupSettingsLink TtsSettings = new("settings/tts", "Settings → Paragraph TTS");
        private static readonly SetupSettingsLink VoiceDesignSettings = new("settings/voice-design", "Settings → Voice design");
        private static readonly SetupSettingsLink TranscriptionSettings = new("settings/transcription", "Settings → Transcription");
        private static readonly SetupSettingsLink SimilaritySettings = new("settings/similarity", "Settings → Similarity");

        /// <summary>How an unmanaged URL is probed for reachability.</summary>
        private enum Reach
        {
            /// <summary>OpenAI-compatible: <c>GET /v1/models</c> answers 2xx.</summary>
            ModelList,

            /// <summary>Any HTTP response to a GET on the base URL.</summary>
            AnyAnswer,
        }

        /// <summary>One config a step uses: its name, base URL and the model it names (null: none).</summary>
        private sealed record UsedConfig(string Name, string? BaseUrl, string? Model);

        /// <summary>The active configs, read once per check.</summary>
        private sealed record ActiveConfigs(
            UsedConfig? Llm,
            IReadOnlyList<UsedConfig> Chain,
            UsedConfig? ParagraphTts,
            UsedConfig? VoiceDesign,
            UsedConfig? Transcription,
            UsedConfig? Similarity)
        {
            public IEnumerable<UsedConfig> All =>
                new[] { Llm, ParagraphTts, VoiceDesign, Transcription, Similarity }.OfType<UsedConfig>().Concat(Chain);
        }

        /// <summary>The AI configs rows, then one Docker services row per registered container.</summary>
        public async Task<IReadOnlyList<SetupCheckRow>> RunAsync(bool dockerOnPath, CancellationToken ct)
        {
            var configs = await ReadActiveConfigsAsync();
            var rows = new List<SetupCheckRow>
            {
                await ConfigRowAsync(SetupCheckIds.Llm, "LLM", ConfigExpected, LlmSettings, Single(configs.Llm), required: true, Reach.ModelList, Describe, ct),
                await ChainRowAsync(configs.Chain, ct),
                await ConfigRowAsync(SetupCheckIds.ParagraphTts, "Paragraph TTS", ConfigExpected, TtsSettings, Single(configs.ParagraphTts), required: true, Reach.ModelList, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.VoiceDesign, "Voice design", ConfigExpected, VoiceDesignSettings, Single(configs.VoiceDesign), required: true, Reach.ModelList, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.Transcription, "Transcription", ConfigExpected, TranscriptionSettings, Single(configs.Transcription), required: false, Reach.AnyAnswer, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.Similarity, "Similarity", ConfigExpected, SimilaritySettings, Single(configs.Similarity), required: false, Reach.AnyAnswer, Describe, ct),
            };
            foreach (var service in deps.Registry.All)
                rows.Add(await ContainerRowAsync(service, configs, dockerOnPath, ct));
            return rows;
        }

        private async Task<ActiveConfigs> ReadActiveConfigsAsync()
        {
            await using var scope = deps.Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var llmSettings = sp.GetRequiredService<LlmSettingsService>();

            var llm = await llmSettings.GetActiveConfigAsync();
            var chain = await llmSettings.GetAttributionChainAsync();
            var tts = await sp.GetRequiredService<ParagraphTtsSettingsService>().GetActiveConfigAsync();
            var design = await sp.GetRequiredService<VoiceDesignSettingsService>().GetActiveConfigAsync();
            var transcription = await sp.GetRequiredService<TranscriptionSettingsService>().GetActiveConfigAsync();
            var similarity = await sp.GetRequiredService<SemanticSimilaritySettingsService>().GetActiveConfigAsync();

            return new ActiveConfigs(
                llm is null ? null : new UsedConfig(llm.Name, ServiceConfigBaseUrls.For(llm), ServiceConfigModelIds.For(llm)),
                [.. chain.Select(s => new UsedConfig(s.Config.Name, ServiceConfigBaseUrls.For(s.Config), ServiceConfigModelIds.For(s.Config)))],
                tts is null ? null : new UsedConfig(tts.Name, ServiceConfigBaseUrls.For(tts), ServiceConfigModelIds.For(tts)),
                design is null ? null : new UsedConfig(design.Name, ServiceConfigBaseUrls.For(design), ServiceConfigModelIds.For(design)),
                transcription is null ? null : new UsedConfig(transcription.Name, ServiceConfigBaseUrls.For(transcription), null),
                similarity is null ? null : new UsedConfig(similarity.Name, ServiceConfigBaseUrls.For(similarity), null));
        }

        // ── AI configs ───────────────────────────────────────────────────────

        private static IReadOnlyList<UsedConfig> Single(UsedConfig? config) => config is null ? [] : [config];

        /// <summary>"Qwen · http://localhost:8080 · qwen-28b".</summary>
        private static string Describe(IReadOnlyList<UsedConfig> configs) =>
            string.Join(" · ", new[] { configs[0].Name, configs[0].BaseUrl, configs[0].Model }.OfType<string>());

        /// <summary>"Gemma (gemma-12b) → Qwen (qwen-28b)".</summary>
        private static string DescribeChain(IReadOnlyList<UsedConfig> configs) =>
            string.Join(" → ", configs.Select(c => c.Model is null ? c.Name : $"{c.Name} ({c.Model})"));

        /// <summary>
        /// The resolved chain is empty only when there's no stored chain and no active LLM config,
        /// which the LLM row already fails; the chain isn't judged twice.
        /// </summary>
        private async Task<SetupCheckRow> ChainRowAsync(IReadOnlyList<UsedConfig> chain, CancellationToken ct)
        {
            if (chain.Count == 0)
                return new SetupCheckRow(
                    SetupCheckIds.AttributionChain, SetupCheckGroup.AiConfigs, "Attribution chain", SetupCheckTier.Quick,
                    SetupCheckResult.NotRun, ChainExpected, "Needs an active LLM config", null,
                    new SetupFixHint($"no active config — choose one in {LlmSettings.Label}", LlmSettings));
            return await ConfigRowAsync(SetupCheckIds.AttributionChain, "Attribution chain", ChainExpected, LlmSettings, chain,
                required: true, Reach.ModelList, DescribeChain, ct);
        }

        private const string ChainExpected = "Every config in the attribution chain has a parseable base URL";
        private const string ConfigExpected = "An active config with a parseable base URL";

        /// <summary>
        /// Every config the step uses has a parseable base URL; one the app doesn't manage must also
        /// answer a reachability probe. A managed URL is judged on its Docker services row instead.
        /// </summary>
        private async Task<SetupCheckRow> ConfigRowAsync(
            string id, string name, string expected, SetupSettingsLink settings, IReadOnlyList<UsedConfig> configs, bool required,
            Reach reach, Func<IReadOnlyList<UsedConfig>, string> describe, CancellationToken ct)
        {
            SetupCheckRow Row(SetupCheckResult result, string found, string? note, SetupFixHint fix) =>
                new(id, SetupCheckGroup.AiConfigs, name, SetupCheckTier.Quick, result, expected, found, note, fix);

            if (configs.Count == 0)
            {
                var noConfig = new SetupFixHint($"no active config — choose one in {settings.Label}", settings);
                return required
                    ? Row(SetupCheckResult.Failed, "No active config", null, noConfig)
                    : Row(SetupCheckResult.NotUsed, "No active config", NoAccuracyScore, noConfig);
            }

            // In a chain that mixes managed and unmanaged configs, the note names the unmanaged ones.
            var distinct = configs.DistinctBy(c => (c.Name, c.BaseUrl)).ToList();
            var unmanaged = distinct.Where(c => c.BaseUrl is not null && deps.Control.Resolve(c.BaseUrl) is null).ToList();
            var note = unmanaged.Count == 0 ? null
                : unmanaged.Count == distinct.Count ? NotManaged
                : $"{string.Join(", ", unmanaged.Select(c => c.Name))}: {NotManaged}";

            foreach (var config in distinct)
            {
                if (config.BaseUrl is null)
                    return Row(SetupCheckResult.Failed, $"{config.Name}: no base URL", null,
                        new SetupFixHint($"Set the config's base URL in {settings.Label}.", settings));
                if (!IsHttpUrl(config.BaseUrl))
                    return Row(SetupCheckResult.Failed, $"{config.Name}: \"{config.BaseUrl}\" isn't a URL", null,
                        new SetupFixHint($"Correct the config's base URL in {settings.Label}.", settings));
                if (deps.Control.Resolve(config.BaseUrl) is not null)
                    continue;

                var (url, error) = await ReachAsync(config.BaseUrl, reach, ct);
                if (error is not null)
                    return Row(SetupCheckResult.Failed, $"{config.Name}: GET {url} failed: {error}", note,
                        new SetupFixHint(
                            $"The app can't reach {config.BaseUrl}: start it where it runs, or correct the config's URL in {settings.Label}.",
                            settings));
            }

            return Row(SetupCheckResult.Passed, describe(configs), note,
                new SetupFixHint($"Change it in {settings.Label}.", settings));
        }

        private static bool IsHttpUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>The URL probed and, when it didn't answer as the probe needs, why.</summary>
        private async Task<(string Url, string? Error)> ReachAsync(string baseUrl, Reach reach, CancellationToken ct)
        {
            var url = reach == Reach.ModelList ? $"{baseUrl.TrimEnd('/')}/v1/models" : baseUrl;
            try
            {
                using var response = await GetAsync(url, ct);
                return reach == Reach.ModelList && !response.IsSuccessStatusCode
                    ? (url, $"{(int)response.StatusCode} {response.ReasonPhrase}")
                    : (url, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (url, Unanswered(ex));
            }
        }

        // ── Docker services ──────────────────────────────────────────────────

        private async Task<SetupCheckRow> ContainerRowAsync(
            DockerAiService service, ActiveConfigs configs, bool dockerOnPath, CancellationToken ct)
        {
            var users = configs.All
                .Where(c => c.BaseUrl is not null && deps.Control.Resolve(c.BaseUrl)?.Name == service.Name)
                .ToList();
            var models = users.Select(c => c.Model).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            var expected = $"{service.ContainerName} running and answering at {service.BaseUrl}"
                + (models.Count > 0 ? $"; GET /v1/models lists {string.Join(", ", models)}" : "");
            var note = deps.Watchdog.Value.Enabled ? null : WatchdogOff;
            var compose = $"docker compose up -d {service.Name}";
            SetupCheckRow Row(SetupCheckResult result, string found, SetupFixHint fix) =>
                new(SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName,
                    SetupCheckTier.Quick, result, expected, found, note, fix);

            if (!dockerOnPath)
                return Row(SetupCheckResult.NotRun, "Needs the docker CLI",
                    new SetupFixHint("Fix the docker CLI row first, then run the Quick check again."));
            if (users.Count == 0)
                return Row(SetupCheckResult.NotUsed, "No active config uses it",
                    new SetupFixHint("Nothing to do: no active config uses this container."));

            switch (await deps.Control.GetStatusAsync(service, ct))
            {
                case AiServiceStatus.NotFound:
                    return Row(SetupCheckResult.Failed, $"No {service.ContainerName} container",
                        new SetupFixHint($"Create it: run {compose} from Infra/.", Copy: compose));
                case AiServiceStatus.Down:
                    return Row(SetupCheckResult.Failed, "Down: the watchdog gave up restarting it",
                        new SetupFixHint(
                            $"Read why with docker logs -f {service.ContainerName}, then run {compose} from Infra/.",
                            Copy: $"docker logs -f {service.ContainerName}"));
                case AiServiceStatus.Starting:
                    return Row(SetupCheckResult.Failed,
                        $"Running, but {service.BaseUrl.TrimEnd('/')}{service.HealthPath} didn't answer",
                        new SetupFixHint(
                            $"The app expects {service.Name} at {service.BaseUrl}: check the port mapping for {service.Name} in Infra/docker-compose.yml.",
                            Copy: "Infra/docker-compose.yml"));
                case AiServiceStatus.Stopped:
                    return Row(SetupCheckResult.NotConfirmed, "Stopped",
                        new SetupFixHint("The Quick check doesn't start containers: run the Full check to confirm it."));
                case AiServiceStatus.Recovering:
                    return Row(SetupCheckResult.NotConfirmed, "The watchdog is restarting it",
                        new SetupFixHint("Wait for the restart, then run the Quick check again."));
                case AiServiceStatus.Ready:
                    break;
                default:
                    return Row(SetupCheckResult.NotRun, "docker didn't answer",
                        new SetupFixHint("Check that Docker Desktop is running, then run the Quick check again."));
            }

            if (models.Count == 0)
                return Row(SetupCheckResult.Passed, "Running and answering", new SetupFixHint("Nothing to do."));

            var url = $"{service.BaseUrl.TrimEnd('/')}/v1/models";
            var modelFix = ModelListFix(service.Name);
            HashSet<string> listed;
            try
            {
                listed = await ListModelsAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Row(SetupCheckResult.Failed, $"GET {url} failed: {Unanswered(ex)}", modelFix);
            }

            var missing = models.Where(m => !listed.Contains(m)).ToList();
            return missing.Count > 0
                ? Row(SetupCheckResult.Failed, $"{service.Name} doesn't list {string.Join(", ", missing)}", modelFix)
                : Row(SetupCheckResult.Passed, $"Running; lists {string.Join(", ", models)}", modelFix);
        }

        /// <summary>Where a model name the server doesn't list is fixed: its model file, or the config.</summary>
        private static SetupFixHint ModelListFix(string serviceName) => serviceName switch
        {
            "llama" => new SetupFixHint(
                $"Add a preset with that name to Infra/llama/config/models.ini, or correct the model in {LlmSettings.Label}.",
                LlmSettings, "Infra/llama/config/models.ini"),
            "audiocpp" => new SetupFixHint(
                "Add that entry to Infra/audiocpp/server.json and restart audiocpp, or correct the config's model in Settings → Paragraph TTS or Voice design.",
                Copy: "Infra/audiocpp/server.json"),
            _ => new SetupFixHint("Correct the config's model in Settings."),
        };

        /// <summary>The <c>id</c>s of an OpenAI-style <c>data[]</c> listing; throws when the GET fails.</summary>
        private async Task<HashSet<string>> ListModelsAsync(string url, CancellationToken ct)
        {
            using var response = await GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        ids.Add(id.GetString()!);
            }
            return ids;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>One GET, bounded by <see cref="ProbeTimeout"/> so a silent host can't hold the check.</summary>
        private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            return await deps.Http.CreateClient().GetAsync(url, timeout.Token);
        }

        private static string Unanswered(Exception ex) => ex is OperationCanceledException
            ? $"no answer within {ProbeTimeout.TotalSeconds:0} s"
            : ex.Message;
    }
}
