using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.AiServers;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Quick check's AI configs and Docker services rows. It judges the active configs and
    /// container statuses the caller read, and reads what llama and audiocpp list; it never starts,
    /// stops or switches anything.
    /// </summary>
    internal sealed class AiServicesCheck(SetupCheckServices deps)
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        private const string NotManaged = "not managed — the app can't start or restart it";
        private const string NoAccuracyScore = "audio is generated without an accuracy score";
        private const string WatchdogOff = "AiWatchdog:Enabled is false: a wedged container won't be restarted";

        /// <summary>How an unmanaged URL is probed for reachability.</summary>
        private enum Reach
        {
            /// <summary>OpenAI-compatible: <c>GET /v1/models</c> answers 2xx.</summary>
            ModelList,

            /// <summary>Any HTTP response to a GET on the base URL.</summary>
            AnyAnswer,
        }

        /// <summary>The AI configs and Docker services rows of the given configs and statuses, read by the caller.</summary>
        public async Task<IReadOnlyList<SetupCheckRow>> RunAsync(
            ActiveAiConfigs configs, IReadOnlyDictionary<string, AiServiceStatus> statuses, bool dockerOnPath, CancellationToken ct)
        {
            static (string, string?, string?) ReadLlm(LlmServerConfig c) => (c.Name, ServiceConfigBaseUrls.For(c), ServiceConfigModelIds.For(c));
            var llm = View(configs.Llm, ReadLlm);
            var chain = configs.Chain.SelectMany(c => View(c, ReadLlm)).ToList();
            var tts = View(configs.ParagraphTts, c => (c.Name, ServiceConfigBaseUrls.For(c), ServiceConfigModelIds.For(c)));
            var design = View(configs.VoiceDesign, c => (c.Name, ServiceConfigBaseUrls.For(c), ServiceConfigModelIds.For(c)));
            var transcription = View(configs.Transcription, c => (c.Name, ServiceConfigBaseUrls.For(c), (string?)null));
            var similarity = View(configs.Similarity, c => (c.Name, ServiceConfigBaseUrls.For(c), (string?)null));

            var rows = new List<SetupCheckRow>
            {
                await ConfigRowAsync(SetupCheckIds.Llm, "LLM", ConfigExpected, SetupSettingsLink.Llm, llm, required: true, Reach.ModelList, Describe, ct),
                await ChainRowAsync(chain, ct),
                await ConfigRowAsync(SetupCheckIds.ParagraphTts, "Paragraph TTS", ConfigExpected, SetupSettingsLink.Tts, tts, required: true, Reach.ModelList, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.VoiceDesign, "Voice design", ConfigExpected, SetupSettingsLink.VoiceDesign, design, required: true, Reach.ModelList, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.Transcription, "Transcription", ConfigExpected, SetupSettingsLink.Transcription, transcription, required: false, Reach.AnyAnswer, Describe, ct),
                await ConfigRowAsync(SetupCheckIds.Similarity, "Similarity", ConfigExpected, SetupSettingsLink.Similarity, similarity, required: false, Reach.AnyAnswer, Describe, ct),
            };
            var all = llm.Concat(tts).Concat(design).Concat(transcription).Concat(similarity).Concat(chain).ToList();
            foreach (var service in deps.Registry.All)
                rows.Add(await ContainerRowAsync(service, all, statuses, dockerOnPath, ct));
            return rows;
        }

        /// <summary>The same rows, each Not run for one reason; nothing is read or probed.</summary>
        public IReadOnlyList<SetupCheckRow> NotRun(string found, SetupFixHint fix)
        {
            SetupCheckRow Config(string id, string name, string expected) =>
                new(id, SetupCheckGroup.AiConfigs, name, SetupCheckTier.Full, SetupCheckResult.NotRun,
                    expected, found, null, fix, Managed: false, Rerun: null);

            var rows = new List<SetupCheckRow>
            {
                Config(SetupCheckIds.Llm, "LLM", ConfigExpected),
                Config(SetupCheckIds.AttributionChain, "Attribution chain", ChainExpected),
                Config(SetupCheckIds.ParagraphTts, "Paragraph TTS", ConfigExpected),
                Config(SetupCheckIds.VoiceDesign, "Voice design", ConfigExpected),
                Config(SetupCheckIds.Transcription, "Transcription", ConfigExpected),
                Config(SetupCheckIds.Similarity, "Similarity", ConfigExpected),
            };
            rows.AddRange(deps.Registry.All.Select(service => new SetupCheckRow(
                SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName,
                SetupCheckTier.Quick, SetupCheckResult.NotRun,
                $"{service.ContainerName} running and answering at {service.BaseUrl}", found, null, fix, Managed: true, Rerun: null)));
            return rows;
        }

        // ── AI configs ───────────────────────────────────────────────────────

        /// <summary>What a row reads of one config: its name, base URL, the model it names (null: none) and its managed service.</summary>
        private sealed record ConfigView(string Name, string? BaseUrl, string? Model, DockerAiService? Service);

        /// <summary>A step's config as its row sees it; none when the step has no active config.</summary>
        private static IReadOnlyList<ConfigView> View<T>(
            ActiveConfig<T>? active, Func<T, (string Name, string? BaseUrl, string? Model)> read) where T : class
        {
            if (active is null)
                return [];
            var (name, baseUrl, model) = read(active.Config);
            return [new ConfigView(name, baseUrl, model, active.Service)];
        }

        /// <summary>"Qwen · http://localhost:8080 · qwen-28b".</summary>
        private static string Describe(IReadOnlyList<ConfigView> configs) =>
            string.Join(" · ", new[] { configs[0].Name, configs[0].BaseUrl, configs[0].Model }.OfType<string>());

        /// <summary>"Gemma (gemma-12b) → Qwen (qwen-28b)".</summary>
        private static string DescribeChain(IReadOnlyList<ConfigView> configs) =>
            string.Join(" → ", configs.Select(c => c.Model is null ? c.Name : $"{c.Name} ({c.Model})"));

        /// <summary>
        /// The resolved chain is empty only when there's no stored chain and no active LLM config,
        /// which the LLM row already fails; the chain isn't judged twice.
        /// </summary>
        private async Task<SetupCheckRow> ChainRowAsync(IReadOnlyList<ConfigView> chain, CancellationToken ct)
        {
            if (chain.Count == 0)
                return new SetupCheckRow(
                    SetupCheckIds.AttributionChain, SetupCheckGroup.AiConfigs, "Attribution chain", SetupCheckTier.Full,
                    SetupCheckResult.NotRun, ChainExpected, "Needs an active LLM config", null,
                    new SetupFixHint($"no active config — choose one in {SetupSettingsLink.Llm.Label}", SetupSettingsLink.Llm, null), Managed: false, Rerun: null);
            return await ConfigRowAsync(SetupCheckIds.AttributionChain, "Attribution chain", ChainExpected, SetupSettingsLink.Llm, chain,
                required: true, Reach.ModelList, DescribeChain, ct);
        }

        private const string ChainExpected = "Every config in the attribution chain has a parseable base URL";
        private const string ConfigExpected = "An active config with a parseable base URL";

        /// <summary>
        /// Every config the step uses has a parseable base URL; one the app doesn't manage must also
        /// answer a reachability probe. A managed URL is judged on its Docker services row instead.
        /// </summary>
        private async Task<SetupCheckRow> ConfigRowAsync(
            string id, string name, string expected, SetupSettingsLink settings, IReadOnlyList<ConfigView> configs, bool required,
            Reach reach, Func<IReadOnlyList<ConfigView>, string> describe, CancellationToken ct)
        {
            // Managed once every config the step uses is on a URL the app manages.
            var managed = false;
            // Full-tier: the Quick check judges the config, the Full check sends it a real request.
            SetupCheckRow Row(SetupCheckResult result, string found, string? note, SetupFixHint fix) =>
                new(id, SetupCheckGroup.AiConfigs, name, SetupCheckTier.Full, result, expected, found, note, fix, managed, Rerun: null);

            if (configs.Count == 0)
            {
                var noConfig = new SetupFixHint($"no active config — choose one in {settings.Label}", settings, null);
                return required
                    ? Row(SetupCheckResult.Failed, "No active config", null, noConfig)
                    : Row(SetupCheckResult.NotUsed, "No active config", NoAccuracyScore, noConfig);
            }

            // In a chain that mixes managed and unmanaged configs, the note names the unmanaged ones.
            var distinct = configs.DistinctBy(c => (c.Name, c.BaseUrl)).ToList();
            var unmanaged = distinct.Where(c => c.BaseUrl is not null && c.Service is null).ToList();
            var note = unmanaged.Count == 0 ? null
                : unmanaged.Count == distinct.Count ? NotManaged
                : $"{string.Join(", ", unmanaged.Select(c => c.Name))}: {NotManaged}";
            managed = distinct.All(c => c.Service is not null);

            foreach (var config in distinct)
            {
                if (config.BaseUrl is null)
                    return Row(SetupCheckResult.Failed, $"{config.Name}: no base URL", null,
                        new SetupFixHint($"Set the config's base URL in {settings.Label}.", settings, null));
                if (!IsHttpUrl(config.BaseUrl))
                    return Row(SetupCheckResult.Failed, $"{config.Name}: \"{config.BaseUrl}\" isn't a URL", null,
                        new SetupFixHint($"Correct the config's base URL in {settings.Label}.", settings, null));
                if (config.Service is not null)
                    continue;

                var (url, error) = await ReachAsync(config.BaseUrl, reach, ct);
                if (error is not null)
                    return Row(SetupCheckResult.Failed, $"{config.Name}: GET {url} failed: {error}", note,
                        new SetupFixHint(
                            $"The app can't reach {config.BaseUrl}: start it where it runs, or correct the config's URL in {settings.Label}.", settings, null));
            }

            return Row(SetupCheckResult.Passed, describe(configs), note,
                new SetupFixHint($"Change it in {settings.Label}.", settings, null));
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
            DockerAiService service, IReadOnlyList<ConfigView> configs, IReadOnlyDictionary<string, AiServiceStatus> statuses,
            bool dockerOnPath, CancellationToken ct)
        {
            var users = configs.Where(c => c.Service?.Name == service.Name).ToList();
            var models = users.Select(c => c.Model).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            var expected = $"{service.ContainerName} running and answering at {service.BaseUrl}"
                + (models.Count > 0 ? $"; GET /v1/models lists {string.Join(", ", models)}" : "");
            var note = deps.Watchdog.Value.Enabled ? null : WatchdogOff;
            SetupCheckRow Row(SetupCheckResult result, string found, SetupFixHint fix) =>
                new(SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName,
                    SetupCheckTier.Quick, result, expected, found, note, fix, Managed: true, Rerun: null);

            if (!dockerOnPath)
                return Row(SetupCheckResult.NotRun, "Needs the docker CLI",
                    new SetupFixHint("Fix the docker CLI row first, then run the Quick check again.", null, null));
            if (users.Count == 0)
                return Row(SetupCheckResult.NotUsed, "No active config uses it",
                    new SetupFixHint("Nothing to do: no active config uses this container.", null, null));

            // A container an active config uses is one of the used services the caller read.
            var containerStatus = statuses[service.Name];
            if (ContainerVerdicts.For(service, containerStatus, SetupCheckTier.Quick) is { } verdict)
                return Row(verdict.Result, verdict.Found, verdict.Fix);

            if (containerStatus == AiServiceStatus.Starting)
            {
                // Running, health not 2xx. No answer at all means nothing listens where the app
                // expects it; an HTTP error (llama's 503 while it loads a model) means not ready yet.
                var health = $"{service.BaseUrl.TrimEnd('/')}{service.HealthPath}";
                var status = await HealthStatusAsync(health, ct);
                if (status is null)
                    return Row(SetupCheckResult.Failed, $"Running, but {health} didn't answer",
                        new SetupFixHint(
                            $"The app expects {service.Name} at {service.BaseUrl}: check the port mapping for {service.Name} in Infra/docker-compose.yml.", null, "Infra/docker-compose.yml"));
                if (status is < 200 or > 299)
                    return Row(SetupCheckResult.NotConfirmed,
                        $"{health} answers {status}: not ready yet (a model may still be loading)",
                        new SetupFixHint("Wait for it to finish loading, then run the Quick check again.", null, null));
                // 2xx: it became ready since the status was read, so it goes on as Ready.
            }

            if (models.Count == 0)
                return Row(SetupCheckResult.Passed, "Running and answering", new SetupFixHint("Nothing to do.", null, null));

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
                $"Add a preset with that name to Infra/llama/config/models.ini, or correct the model in {SetupSettingsLink.Llm.Label}.", SetupSettingsLink.Llm, "Infra/llama/config/models.ini"),
            "audiocpp" => new SetupFixHint(
                "Add that entry to Infra/audiocpp/server.json and restart audiocpp, or correct the config's model in Settings → Paragraph TTS or Voice design.", null, "Infra/audiocpp/server.json"),
            _ => new SetupFixHint("Correct the config's model in Settings.", null, null),
        };

        /// <summary>The <c>id</c>s of an OpenAI-style <c>data[]</c> listing; throws when the GET fails.</summary>
        private async Task<HashSet<string>> ListModelsAsync(string url, CancellationToken ct)
        {
            using var response = await GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ModelListing.Parse(doc.RootElement).Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The HTTP status a health GET answers; null when nothing answered.</summary>
        private async Task<int?> HealthStatusAsync(string url, CancellationToken ct)
        {
            try
            {
                using var response = await GetAsync(url, ct);
                return (int)response.StatusCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return null;
            }
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
