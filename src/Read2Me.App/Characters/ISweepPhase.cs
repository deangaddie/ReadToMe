using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Characters;

public interface ISweepPhase<TWork>
{
    string Operation { get; }
    bool DrivesLlm { get; }
    string DisplayName(TWork item);
    Task<IReadOnlyList<TWork>> PlanAsync(PhaseDeps deps, ProjectFolderId folder, CancellationToken ct);
    Task<PhaseStepOutcome> RunStepAsync(TWork item, PhaseDeps deps, CancellationToken ct);
}

/// <param name="Mutations">The write side every durable change in a sweep crosses (ADR 0007).</param>
/// <param name="VoiceAudio">
/// The two gestures that also move a file — deleting a Voice, and making one designed — go here
/// instead, because the file has to be removed after the commit rather than inside it.
/// </param>
public sealed record PhaseDeps(
    IProjectReader Reader,
    BookMutations Mutations,
    IVoiceAudioRemover VoiceAudio,
    Read2Me.App.Services.VoiceOrchestrator Orchestrator);

public sealed record PhaseStepOutcome(
    bool Ok,
    VoiceBatchEvent? Update,
    string? FailReason);
