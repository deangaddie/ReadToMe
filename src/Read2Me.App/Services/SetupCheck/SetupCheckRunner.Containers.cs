using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What a Full check run reads as it starts and its steps share, and the containers it drives:
    /// each started when stopped, a rival GPU one stopped by the sweep, and afterwards each put back
    /// as it was found, llama with the model it had, except one whose check failed, which is left as
    /// it is for its logs. A URL the app doesn't manage is never started, stopped or put back.
    /// </summary>
    public sealed partial class SetupCheckRunner
    {
        /// <summary>
        /// The active configs the steps use, each with its managed service, and the run's state: the
        /// rows as the run found them, what the steps hand on (the transcript) and the containers driven.
        /// </summary>
        private sealed class FullContext(ActiveAiConfigs configs)
        {
            public ActiveAiConfigs Configs { get; } = configs;

            /// <summary>Each distinct model the active LLM config and the attribution chain name, active first.</summary>
            public IReadOnlyList<LlmTarget> Llm { get; } = LlmTargets(configs);

            public IReadOnlyDictionary<string, SetupCheckRow> Before { get; set; } = new Dictionary<string, SetupCheckRow>();

            /// <summary>A row as the run found it; a bare one when no Quick check had made it yet.</summary>
            public SetupCheckRow Row(string id, SetupCheckGroup group, string name) =>
                Before.TryGetValue(id, out var row) ? row
                : new(id, group, name, SetupCheckTier.Full, SetupCheckResult.NotRun, "", "", null, new SetupFixHint("", null, null), Managed: false, Rerun: null);

            /// <summary>The sample clip's transcript, once the transcription check has one.</summary>
            public string? Transcript { get; set; }

            /// <summary>The containers the run drove, in the order it first did.</summary>
            public List<ContainerUse> Containers { get; } = [];
        }

        /// <summary>
        /// One driven container: whether it ran before the run touched it, whether it runs now, whether
        /// the run ever started it, whether its check failed (and that verdict, which later visits keep),
        /// and for llama the model it had loaded.
        /// </summary>
        private sealed class ContainerUse(DockerAiService service)
        {
            public DockerAiService Service { get; } = service;
            public bool WasRunning { get; init; }
            public bool Running { get; set; }
            public bool Started { get; set; }
            public bool Failed { get; set; }
            public SetupCheckRow? Row { get; set; }
            public string? LoadedModel { get; init; }

            /// <summary>A sweep stopped it to free the GPU.</summary>
            public bool Swept { get; set; }
        }

        /// <summary>One target per distinct model on each endpoint, so each is exercised once.</summary>
        private static List<LlmTarget> LlmTargets(ActiveAiConfigs configs)
        {
            var targets = new List<LlmTarget>();
            void Add(ActiveConfig<LlmServerConfig> active, bool isActive)
            {
                var (config, service) = active;
                var baseUrl = ServiceConfigBaseUrls.For(config);
                var endpoint = service?.Name ?? baseUrl?.TrimEnd('/');
                var model = ServiceConfigModelIds.For(config);
                var target = targets.FirstOrDefault(t =>
                    string.Equals(t.Service?.Name ?? t.BaseUrl?.TrimEnd('/'), endpoint, StringComparison.OrdinalIgnoreCase)
                    && t.Model == model);
                if (target is null)
                    targets.Add(target = new LlmTarget(config, baseUrl, service));
                if (isActive)
                    target.Active = true;
                else
                    target.InChain = true;
            }

            if (configs.Llm is { } llm)
                Add(llm, isActive: true);
            foreach (var entry in configs.Chain)
                Add(entry, isActive: false);
            return targets;
        }

        /// <summary>The container's use, made as the run first touches it: running then or not, and llama's loaded model.</summary>
        private async Task<ContainerUse> TrackAsync(FullContext context, DockerAiService service, bool running)
        {
            if (context.Containers.FirstOrDefault(c => c.Service.Name == service.Name) is { } known)
                return known;
            var use = new ContainerUse(service)
            {
                WasRunning = running,
                Running = running,
                LoadedModel = running && service.Name == Llama ? await LoadedModelAsync(service) : null,
            };
            context.Containers.Add(use);
            return use;
        }

        /// <summary>
        /// Readies a managed container for a step: started when stopped, used as it is when running.
        /// Answers its Docker services row's verdict, and whether the step can send its request. Once a
        /// check has failed the container, later visits keep that verdict but still send their request.
        /// </summary>
        private async Task<(SetupCheckRow Row, bool Ready, ContainerUse Use)> ReadyContainerAsync(
            FullContext context, DockerAiService service)
        {
            var status = await aiServices.Control.GetStatusAsync(service, CancellationToken.None);
            var use = await TrackAsync(context, service,
                running: status is AiServiceStatus.Ready or AiServiceStatus.Starting or AiServiceStatus.Recovering);

            var row = context.Row(SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName);
            SetupCheckRow Verdict(SetupCheckResult result, string found, SetupFixHint fix) =>
                row with { Result = result, Found = found, Fix = fix };
            (SetupCheckRow, bool, ContainerUse) Failed(string found, SetupFixHint fix)
            {
                use.Failed = true;
                use.Row = Verdict(SetupCheckResult.Failed, found, fix);
                return (use.Row, false, use);
            }

            // A Full verdict is Passed or Failed, so any final one here fails the container.
            if (ContainerVerdicts.For(service, status, SetupCheckTier.Full) is { } verdict)
                return Failed(verdict.Found, verdict.Fix);

            switch (status)
            {
                case AiServiceStatus.Stopped:
                    var start = await aiServices.Control.StartAsync(service, CancellationToken.None);
                    use.Started = true;
                    if (!start.Succeeded)
                        return Failed($"Didn't start: {start.Error}", ContainerVerdicts.RestartFix(service));
                    use.Running = true;
                    break;
                case AiServiceStatus.Starting:
                    // Running but not answering yet: start waits for its health and warm-up, and
                    // leaves a running container running, so it was never the run's to stop.
                    var ready = await aiServices.Control.StartAsync(service, CancellationToken.None);
                    if (!ready.Succeeded)
                        return Failed($"Never became ready: {ready.Error}", ContainerVerdicts.RestartFix(service));
                    break;
            }
            if (use is { Failed: true, Row: { } failed })
                return (failed, true, use);
            return (Verdict(SetupCheckResult.Passed,
                use.Started ? "Started for the Full check, and answered" : "Running, and answered",
                new SetupFixHint("Nothing to do.", null, null)), true, use);
        }

        /// <summary>
        /// Puts every driven container back as it was found: one that wasn't running is stopped, then
        /// one that was is started again (stops first, so a GPU one has the card to itself), and llama
        /// reloads the model it had. One whose check failed is left as it is. Answers the "put back" line.
        /// </summary>
        private async Task<string> PutBackAsync(FullContext context)
        {
            if (context.Containers.Count == 0)
                return "Nothing to put back: the check drove no containers.";

            var parts = new Dictionary<ContainerUse, string>();
            foreach (var use in context.Containers.Where(u => !u.Failed && !u.WasRunning))
            {
                var name = use.Service.ContainerName;
                if (!use.Running)
                {
                    parts[use] = $"{name} stopped again";
                    continue;
                }
                var stop = await aiServices.Control.ShutdownAsync(use.Service, CancellationToken.None);
                parts[use] = stop.Succeeded ? $"{name} stopped again" : $"{name} couldn't be stopped again: {stop.Error}";
            }
            foreach (var use in context.Containers.Where(u => !u.Failed && u.WasRunning))
            {
                var name = use.Service.ContainerName;
                string part;
                if (use.Running)
                    part = $"{name} left running, as it was";
                else if (use.Service.UsesGpu
                    && context.Containers.FirstOrDefault(u => u is { Failed: true, Running: true } && u.Service.UsesGpu) is { } holding)
                {
                    // The one card is held by a failed container left as it is for its logs.
                    parts[use] = $"{name} not started again: {holding.Service.ContainerName} holds the GPU";
                    continue;
                }
                else
                {
                    var start = await aiServices.Control.StartAsync(use.Service, CancellationToken.None);
                    if (!start.Succeeded)
                    {
                        parts[use] = $"{name} couldn't be started again: {start.Error}";
                        continue;
                    }
                    part = $"{name} started again";
                }
                parts[use] = use.LoadedModel is { } model ? $"{part}, {await ReloadAsync(use.Service, model)}" : part;
            }

            // A failed container is left as it is, unless a later GPU visit's sweep had to stop it;
            // docker logs reads a stopped container too.
            var lines = context.Containers.Select(use =>
                parts.TryGetValue(use, out var part) ? part
                : use is { Swept: true, Running: false }
                    ? $"{use.Service.ContainerName} left stopped (its check failed; a later GPU check's sweep stopped it, and docker logs still reads it)"
                    : $"{use.Service.ContainerName} left as it is (its check failed)");
            return $"Put back: {string.Join("; ", lines)}.";
        }

        /// <summary>What a container start takes: docker start, the health poll and the warm-up.</summary>
        private const int StartSeconds = 20;

        /// <summary>
        /// How long a Full check is likely to take, from the containers as they are now: each step's
        /// own time (a model load for each GPU one), a start for each container a step must start,
        /// one for each running GPU container another GPU step's sweep stops and putting back starts
        /// again, and a model load for llama reloading the model it has. No statuses (docker isn't on
        /// PATH) leaves the steps' own time.
        /// </summary>
        private int EstimateSeconds(
            ActiveAiConfigs configs, IReadOnlyList<FullStep> plan, IReadOnlyDictionary<string, AiServiceStatus> statuses)
        {
            var seconds = plan.Sum(s => s.EstimateSeconds);
            var used = configs.UsedServices;
            var usedGpu = used.Where(s => s.UsesGpu).ToList();
            foreach (var service in aiServices.Registry.All)
            {
                if (!statuses.TryGetValue(service.Name, out var status))
                    continue;
                if (status == AiServiceStatus.Stopped && used.Any(s => s.Name == service.Name))
                    seconds += StartSeconds;
                if (status is not (AiServiceStatus.Ready or AiServiceStatus.Starting) || !service.UsesGpu)
                    continue;
                if (usedGpu.Any(s => s.Name != service.Name))
                    seconds += StartSeconds;
                if (service.Name == Llama)
                    seconds += ModelSwitchSeconds;
            }
            return seconds;
        }

        /// <summary>llama loads the model it had again: a 1-token completion naming it.</summary>
        private async Task<string> ReloadAsync(DockerAiService service, string model)
        {
            try
            {
                await CompleteOneTokenAsync(new LlmServerConfig { Name = model, BaseUrl = service.BaseUrl, Model = model });
                return $"with {model} loaded again";
            }
            catch (Exception ex)
            {
                return $"but {model} didn't load again: {ex.Message}";
            }
        }
    }
}
