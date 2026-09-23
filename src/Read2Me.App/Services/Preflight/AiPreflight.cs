using MudBlazor;
using Read2Me.App.Shared;

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
    /// The Blazor gate: plans via <see cref="IAiPreflightPlanner"/> and, when there is anything to
    /// do, shows <c>AiPreflightDialog</c> to reconcile (stopping unneeded running GPU services
    /// first, then starting what is missing). Fast path — all required Ready, no rival GPU
    /// container, or nothing managed — never touches the dialog service. The Angular UI reaches
    /// the same planner and runner through <c>/api/preflight</c>.
    /// </summary>
    public sealed class AiPreflight(IAiPreflightPlanner planner, IDialogService dialogs) : IAiPreflight
    {
        public async Task<bool> EnsureReadyAsync(AiTaskKind task, CancellationToken ct = default)
        {
            var plan = await planner.BuildPlanAsync(task, ct);
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
    }
}
