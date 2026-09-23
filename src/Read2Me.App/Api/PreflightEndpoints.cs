using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Live;
using Read2Me.App.Services.Preflight;

namespace Read2Me.App.Api
{
    /// <summary>A required service that is not Ready, with its status (<c>AiServiceStatus</c> member name).</summary>
    public sealed record PreflightServiceDto(string Name, string Status);

    /// <summary>A running GPU service the task does not need; stopped first to free VRAM.</summary>
    public sealed record PreflightConflictDto(string Name, string Reason);

    /// <summary><c>ready</c> means nothing to start and nothing to stop: the task may run now.</summary>
    public sealed record PreflightPlanDto(
        bool Ready,
        IReadOnlyList<PreflightServiceDto> ToStart,
        IReadOnlyList<PreflightConflictDto> Conflicts);

    /// <param name="ConnectionId">The live hub connection that receives the run's <c>preflight</c> messages.</param>
    public sealed record PreflightRunRequest(string? ConnectionId = null);

    public sealed record PreflightRunResponse(string Run);

    /// <summary>
    /// <c>/api/preflight/{taskKind}</c> (Angular ticket 25): the readiness gate every AI action
    /// passes, as plan + run so the Angular sheet shows the same required services, conflicts and
    /// per-service progress as the Blazor dialog. Task kinds are <see cref="AiTaskKind"/> member
    /// names, matched without case.
    /// </summary>
    public static class PreflightEndpoints
    {
        public const string GpuConflictReason = "GPU: one model at a time";

        public static void MapPreflightEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/preflight");

            group.MapPost("/{taskKind}/plan", PlanAsync)
                .WithSummary("What must happen before the task may run: { ready, toStart: [{ name, status }], conflicts: [{ name, reason }] }. ready = nothing to do. 400 for an unknown task kind (CharacterAttribution, AudioGeneration, VoicePromptGeneration, CharacterDiscovery, VoiceDesignAudio, Transcription, BookEdit).");

            group.MapPost("/{taskKind}/run", RunAsync)
                .WithSummary("Plan again and carry it out in the background (202 { run }): conflicts stopped, then required services started one at a time. Progress goes to the connectionId in the body as preflight { kind: stage, run, name, stage, error? } then { kind: done, run, ok, reason? }.");
        }

        private static async Task<IResult> PlanAsync(string taskKind, IAiPreflightPlanner planner, CancellationToken ct)
        {
            if (!TryParse(taskKind, out var task))
                return UnknownTask(taskKind);

            var plan = await planner.BuildPlanAsync(task, ct);
            return Results.Ok(ToDto(plan));
        }

        private static async Task<IResult> RunAsync(
            string taskKind, PreflightRunRequest? request, IAiPreflightPlanner planner, PreflightRunCoordinator runs,
            CancellationToken ct)
        {
            if (!TryParse(taskKind, out var task))
                return UnknownTask(taskKind);

            // Re-planned here rather than trusting a plan the client read a moment ago: a container
            // may have moved since, and the run must stop and start what is true now.
            var plan = await planner.BuildPlanAsync(task, ct);
            var run = runs.Start(plan, request?.ConnectionId);
            return Results.Accepted(value: new PreflightRunResponse(run));
        }

        private static bool TryParse(string taskKind, out AiTaskKind task) =>
            Enum.TryParse(taskKind, ignoreCase: true, out task) && Enum.IsDefined(task);

        private static IResult UnknownTask(string taskKind) =>
            Results.Problem(
                $"Unknown task kind '{taskKind}'. Use one of: {string.Join(", ", Enum.GetNames<AiTaskKind>())}.",
                statusCode: StatusCodes.Status400BadRequest);

        public static PreflightPlanDto ToDto(AiPreflightPlan plan) => new(
            plan.NothingToDo,
            plan.ToStart.Select(i => new PreflightServiceDto(i.Service.Name, i.Status.ToString())).ToList(),
            plan.Conflicts.Select(c => new PreflightConflictDto(c.Name, GpuConflictReason)).ToList());
    }
}
