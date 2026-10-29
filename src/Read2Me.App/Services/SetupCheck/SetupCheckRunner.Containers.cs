using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What a Full check run reads as it starts and its steps share, and the containers it drives:
    /// each started when stopped, and afterwards put back as it was found, except one whose check
    /// failed, which is left as it is for its logs. A URL the app doesn't manage is never started,
    /// stopped or put back.
    /// </summary>
    public sealed partial class SetupCheckRunner
    {
        /// <summary>
        /// The active configs the steps use, the managed service behind each (null: not managed), the
        /// rows as the run found them, what the steps hand on (the transcript) and the containers driven.
        /// </summary>
        private sealed class FullContext
        {
            public TranscriptionServiceConfig? Transcription { get; init; }
            public DockerAiService? Whisper { get; init; }
            public SemanticSimilarityServiceConfig? Similarity { get; init; }
            public DockerAiService? SimilarityService { get; init; }

            public IReadOnlyDictionary<string, SetupCheckRow> Before { get; set; } = new Dictionary<string, SetupCheckRow>();

            /// <summary>A row as the run found it; a bare one when no Quick check had made it yet.</summary>
            public SetupCheckRow Row(string id, SetupCheckGroup group, string name) =>
                Before.TryGetValue(id, out var row) ? row
                : new(id, group, name, SetupCheckTier.Full, SetupCheckResult.NotRun, "", "", null, new SetupFixHint(""), Managed: false);

            /// <summary>The sample clip's transcript, once the transcription check has one.</summary>
            public string? Transcript { get; set; }

            /// <summary>The containers the run drove, in the order it first did.</summary>
            public List<ContainerUse> Containers { get; } = [];
        }

        /// <summary>One driven container: whether the run started it, and whether its check failed.</summary>
        private sealed class ContainerUse(DockerAiService service)
        {
            public DockerAiService Service { get; } = service;
            public bool Started { get; set; }
            public bool Failed { get; set; }
        }

        private async Task<FullContext> ReadFullContextAsync()
        {
            await using var scope = aiServices.Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var transcription = await sp.GetRequiredService<TranscriptionSettingsService>().GetActiveConfigAsync();
            var similarity = await sp.GetRequiredService<SemanticSimilaritySettingsService>().GetActiveConfigAsync();
            return new FullContext
            {
                Transcription = transcription,
                Whisper = ManagedService(transcription is null ? null : ServiceConfigBaseUrls.For(transcription)),
                Similarity = similarity,
                SimilarityService = ManagedService(similarity is null ? null : ServiceConfigBaseUrls.For(similarity)),
            };
        }

        private DockerAiService? ManagedService(string? baseUrl) =>
            string.IsNullOrWhiteSpace(baseUrl) ? null : aiServices.Control.Resolve(baseUrl);

        /// <summary>
        /// Readies a managed container for a step: started when stopped, used as it is when running.
        /// Answers its Docker services row's verdict, and whether the step can send its request.
        /// </summary>
        private async Task<(SetupCheckRow Row, bool Ready, ContainerUse Use)> ReadyContainerAsync(
            FullContext context, DockerAiService service)
        {
            var use = context.Containers.FirstOrDefault(c => c.Service.Name == service.Name);
            if (use is null)
                context.Containers.Add(use = new ContainerUse(service));

            var row = context.Row(SetupCheckIds.Container(service.Name), SetupCheckGroup.DockerServices, service.ContainerName);
            SetupCheckRow Verdict(SetupCheckResult result, string found, SetupFixHint fix) =>
                row with { Result = result, Found = found, Fix = fix };
            (SetupCheckRow, bool, ContainerUse) Failed(string found, SetupFixHint fix)
            {
                use.Failed = true;
                return (Verdict(SetupCheckResult.Failed, found, fix), false, use);
            }

            switch (await aiServices.Control.GetStatusAsync(service, CancellationToken.None))
            {
                case AiServiceStatus.NotFound:
                    return Failed($"No {service.ContainerName} container", AiServicesCheck.CreateFix(service));
                case AiServiceStatus.Down:
                    return Failed("Down: the watchdog gave up restarting it", AiServicesCheck.RestartFix(service));
                case AiServiceStatus.Unknown:
                    return Failed("docker didn't answer",
                        new SetupFixHint("Check that Docker Desktop is running, then run the Full check again."));
                case AiServiceStatus.Stopped when !use.Started:
                    var start = await aiServices.Control.StartAsync(service, CancellationToken.None);
                    use.Started = true;
                    if (!start.Succeeded)
                        return Failed($"Didn't start: {start.Error}", AiServicesCheck.RestartFix(service));
                    break;
                case AiServiceStatus.Starting:
                    // Running but not answering yet: start waits for its health and warm-up, and
                    // leaves a running container running, so it was never the run's to stop.
                    var ready = await aiServices.Control.StartAsync(service, CancellationToken.None);
                    if (!ready.Succeeded)
                        return Failed($"Never became ready: {ready.Error}", AiServicesCheck.RestartFix(service));
                    break;
            }
            return (Verdict(SetupCheckResult.Passed,
                use.Started ? "Started for the Full check, and answered" : "Running, and answered",
                new SetupFixHint("Nothing to do.")), true, use);
        }

        /// <summary>
        /// Puts every driven container back: one the run started is stopped again; one that was
        /// running is left running; one whose check failed is left as it is. Answers the "put back" line.
        /// </summary>
        private async Task<string> PutBackAsync(FullContext context)
        {
            if (context.Containers.Count == 0)
                return "Nothing to put back: the Full check drove no containers.";

            var parts = new List<string>();
            foreach (var use in context.Containers)
            {
                var name = use.Service.ContainerName;
                if (use.Failed)
                    parts.Add($"{name} left as it is (its check failed)");
                else if (!use.Started)
                    parts.Add($"{name} left running, as it was");
                else
                {
                    var stop = await aiServices.Control.ShutdownAsync(use.Service, CancellationToken.None);
                    parts.Add(stop.Succeeded ? $"{name} stopped again" : $"{name} couldn't be stopped again: {stop.Error}");
                }
            }
            return $"Put back: {string.Join("; ", parts)}.";
        }
    }
}
