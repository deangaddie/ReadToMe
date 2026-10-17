using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Checkpoints;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>Stores, replaces or deletes one Pipeline step's checkpoint row.</summary>
public sealed class SetCheckpointMutationImplementation(TimeProvider clock)
    : IBookMutationImplementation<SetCheckpointMutation>
{
    private static readonly BookMutationEffects Changed = new()
    {
        Scope = BookMutationScope.Exact,
        Facets = BookFacets.Checkpoints,
    };

    public async Task<BookMutationEffects> ApplyAsync(
        SetCheckpointMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (CheckpointMatrix.Refusal(mutation.Step, mutation.State) is { } refusal)
            throw new BookMutationRejectedException(BookMutationRejection.Validation, refusal);
        // A checkpoint says the user looked at the Book; with none there is nothing to have looked at.
        if (!await db.Volumes.AnyAsync(ct))
            throw new BookMutationRejectedException(BookMutationRejection.Validation, "Read the book first.");

        var row = await db.StepCheckpoints.FindAsync([mutation.Step], ct);

        if (mutation.State is not { } state)
        {
            if (row is null) return BookMutationEffects.Nothing;
            db.StepCheckpoints.Remove(row);
            return Changed;
        }

        // A stale row asked again for the state it holds is a reconfirm, not a repeat.
        if (row is { Stale: false } && row.State == state)
            return BookMutationEffects.Nothing;

        if (row is null)
        {
            row = new StepCheckpoint { Step = mutation.Step };
            db.StepCheckpoints.Add(row);
        }

        row.State = state;
        row.Stale = false;
        row.At = clock.GetUtcNow();
        return Changed;
    }
}
