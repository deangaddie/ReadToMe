using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>A Docker services row's final verdict: its result, what was found and the fix.</summary>
    internal sealed record SetupContainerVerdict(SetupCheckResult Result, string Found, SetupFixHint Fix);

    /// <summary>
    /// What a container's Managed service status means for its Docker services row, by tier: a final
    /// verdict, or null to proceed. The Quick check proceeds on Starting and Ready (it probes health and
    /// the model listing); the Full check also proceeds on Stopped (it starts the container). Verdicts
    /// that come from probing or acting stay with the code that probes or acts.
    /// <para>
    /// A Full verdict is Passed or Failed. <c>WithFullVerdicts</c> lets any Full verdict that isn't
    /// Failed stand over later Quick checks, and Failed keeps the container as it is during put back,
    /// so a status the Full check can't act on (Unknown, Recovering) fails rather than being Not run or
    /// Not confirmed.
    /// </para>
    /// </summary>
    internal static class ContainerVerdicts
    {
        public static SetupContainerVerdict? For(DockerAiService service, AiServiceStatus status, SetupCheckTier tier)
        {
            var full = tier == SetupCheckTier.Full;
            var check = full ? "Full" : "Quick";
            return status switch
            {
                AiServiceStatus.NotFound => new(SetupCheckResult.Failed, $"No {service.ContainerName} container", CreateFix(service)),
                AiServiceStatus.Down => new(SetupCheckResult.Failed, "Down: the watchdog gave up restarting it", RestartFix(service)),
                AiServiceStatus.Recovering => new(full ? SetupCheckResult.Failed : SetupCheckResult.NotConfirmed,
                    "The watchdog is restarting it", new SetupFixHint($"Wait for the restart, then run the {check} check again.")),
                AiServiceStatus.Stopped when !full => new(SetupCheckResult.NotConfirmed, "Stopped",
                    new SetupFixHint("The Quick check doesn't start containers: run the Full check to confirm it.")),
                AiServiceStatus.Stopped or AiServiceStatus.Starting or AiServiceStatus.Ready => null,
                _ => new(full ? SetupCheckResult.Failed : SetupCheckResult.NotRun, "docker didn't answer",
                    new SetupFixHint($"Check that Docker Desktop is running, then run the {check} check again.")),
            };
        }

        /// <summary>A container that doesn't exist: create it with compose.</summary>
        public static SetupFixHint CreateFix(DockerAiService service) => new(
            $"Create it: run docker compose up -d {service.Name} from Infra/.", Copy: $"docker compose up -d {service.Name}");

        /// <summary>A container that gave up or won't start: read why, then bring it up with compose.</summary>
        public static SetupFixHint RestartFix(DockerAiService service) => new(
            $"Read why with docker logs -f {service.ContainerName}, then run docker compose up -d {service.Name} from Infra/.",
            Copy: $"docker logs -f {service.ContainerName}");

        /// <summary>A running container that didn't answer a request: read why.</summary>
        public static SetupFixHint LogsFix(DockerAiService service) => new(
            $"Read why with docker logs -f {service.ContainerName}.", Copy: $"docker logs -f {service.ContainerName}");
    }
}
