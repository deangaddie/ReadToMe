using MudBlazor;
using Read2Me.App.Shared;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>
    /// Gate a task calls before touching AI endpoints. True means go (everything Ready, unmanaged,
    /// or the user started what was missing); false means the user cancelled or a start failed.
    /// </summary>
    public interface IAiPreflight
    {
        Task<bool> EnsureReadyAsync(AiTaskKind task, CancellationToken ct = default);
    }

    /// <summary>
    /// Checks the task's required services and, when something is not Ready or a GPU task has other
    /// GPU containers still up, shows <c>AiPreflightDialog</c> to reconcile (stopping unneeded running
    /// GPU services first, then starting what is missing). Fast path — all required Ready, no rival
    /// GPU container, or nothing managed — never touches the dialog service.
    /// </summary>
    public sealed class AiPreflight(
        IAiTaskRequirementsResolver resolver,
        IAiServiceControl control,
        DockerAiServiceRegistry registry,
        IDialogService dialogs) : IAiPreflight
    {
        public async Task<bool> EnsureReadyAsync(AiTaskKind task, CancellationToken ct = default)
        {
            var plan = await BuildPlanAsync(task, ct);
            if (plan.NothingToDo)
                return true;

            var parameters = new DialogParameters<AiPreflightDialog>
            {
                { d => d.Plan, plan },
            };
            var options = new DialogOptions
            {
                BackdropClick = false,
                CloseOnEscapeKey = false,
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
            };
            var dialog = await dialogs.ShowAsync<AiPreflightDialog>("AI services required", parameters, options);
            var result = await dialog.Result;
            return result is { Canceled: false };
        }

        /// <summary>
        /// Required services not Ready, plus — for any GPU-using task — running GPU services the task
        /// does not need (swept even when everything required is Ready). Internal so tests can assert
        /// plans without a dialog service.
        /// </summary>
        internal async Task<AiPreflightPlan> BuildPlanAsync(AiTaskKind task, CancellationToken ct)
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
