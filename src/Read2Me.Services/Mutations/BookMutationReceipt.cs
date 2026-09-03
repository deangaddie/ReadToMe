using Read2Me.Core.Models;

namespace Read2Me.Services.Mutations;

/// <summary>
/// The factual record of one committed Book mutation: which project and which mutation, the
/// process-local revision the commit produced, and the effects the implementation reported.
/// <para>
/// A receipt carries facts, not entity patches and not instructions. In particular it carries no
/// selection verdict: whether a Folder Selection or an Audio Item Selection survives is recomputed
/// by the reader against the new revision, because that is read-side semantics (ADR 0007).
/// </para>
/// </summary>
public sealed record BookMutationReceipt(
    ProjectFolderId FolderId,
    string MutationName,
    Guid MutationId,
    long Revision,
    BookMutationEffects Effects);
