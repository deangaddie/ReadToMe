using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.App.Characters;

public interface ISweepPhase<TWork>
{
    string Operation { get; }
    bool DrivesLlm { get; }
    string DisplayName(TWork item);
    Task<IReadOnlyList<TWork>> PlanAsync(PhaseDeps deps, ProjectFolderId folder, CancellationToken ct);
    Task<PhaseStepOutcome> RunStepAsync(TWork item, PhaseDeps deps, CancellationToken ct);
}

public sealed record PhaseDeps(
    IProjectReader Reader,
    IBookCommandHandler CommandHandler,
    Read2Me.App.Services.VoiceOrchestrator Orchestrator);

public sealed record PhaseStepOutcome(
    bool Ok,
    VoiceBatchEvent? Update,
    string? FailReason);
