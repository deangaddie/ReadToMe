using Read2Me.Services.Health;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>What pre-flight would have to do before a task may touch its AI endpoints.</summary>
    public interface IAiPreflightPlanner
    {
        Task<AiPreflightPlan> BuildPlanAsync(AiTaskKind task, CancellationToken ct);
    }

    /// <summary>
    /// Builds the <see cref="AiPreflightPlan"/> for a task without any UI: required services not
    /// Ready, plus — for any GPU-using task — running GPU services the task does not need (swept
    /// even when everything required is Ready). Shared by the Blazor gate (<see cref="AiPreflight"/>)
    /// and the HTTP preflight endpoints so both UIs reconcile the same way.
    /// </summary>
    public sealed class AiPreflightPlanner(
        IAiTaskRequirementsResolver resolver,
        IAiServiceControl control,
        DockerAiServiceRegistry registry) : IAiPreflightPlanner
    {
        public async Task<AiPreflightPlan> BuildPlanAsync(AiTaskKind task, CancellationToken ct)
        {
            var urls = await resolver.GetRequiredBaseUrlsAsync(task, ct);
            var required = urls
                .Select(control.Resolve)
                .OfType<DockerAiService>()
                .DistinctBy(s => s.Name)
                .ToList();

            var toStart = new List<AiPreflightItem>();
            foreach (var service in required)
            {
                var status = await control.GetStatusAsync(service, ct);
                if (status != AiServiceStatus.Ready)
                    toStart.Add(new AiPreflightItem(service, status));
            }

            // Any GPU task must run alone: the single 8 GB card fits ~one model. Sweep for other
            // GPU containers still up whenever a required service uses the GPU — NOT only when
            // something needs starting. A TTS server that already answers its health check has not
            // necessarily loaded its model onto a GPU another container (e.g. a leftover llama) is
            // holding, so its VRAM must be freed first even on the all-Ready path.
            var conflicts = new List<DockerAiService>();
            if (required.Any(s => s.UsesGpu))
            {
                var requiredNames = required.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in registry.All.Where(s => s.UsesGpu && !requiredNames.Contains(s.Name)))
                {
                    var status = await control.GetStatusAsync(candidate, ct);
                    if (status is AiServiceStatus.Ready or AiServiceStatus.Starting)
                        conflicts.Add(candidate);
                }
            }

            return new AiPreflightPlan(toStart, conflicts);
        }
    }
}
