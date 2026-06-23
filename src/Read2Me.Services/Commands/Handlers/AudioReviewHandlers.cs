using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using EntityState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Services.Commands.Handlers;

public sealed class SetAudioReviewHandler(ProjectDbSession session) : ICommandHandler<SetAudioReviewCommand>
{
    public async Task<Guid?> HandleAsync(SetAudioReviewCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var existing = await db.AudioReviews
            .FirstOrDefaultAsync(r => r.ParagraphItemId == c.ParagraphItemId, ct);

        // Row presence is the signal: a row exists iff a stage failed. Both ok ⇒ remove.
        if (c.NormalizeOk && c.VerifyOk)
        {
            if (existing != null)
            {
                db.AudioReviews.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            return null;
        }

        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new AudioReview
            {
                Id = Guid.NewGuid(),
                ParagraphItemId = c.ParagraphItemId,
                CreatedUtc = now,
            };
            db.AudioReviews.Add(existing);
        }

        // Always reset to NeedsReview on a fresh failure, clearing any prior Dismissed.
        existing.State = EntityState.NeedsReview;
        existing.NormalizeOk = c.NormalizeOk;
        existing.NormalizeReason = c.NormalizeReason;
        existing.VerifyOk = c.VerifyOk;
        existing.Wer = c.Wer;
        existing.VerifyReason = c.VerifyReason;
        existing.Transcript = c.Transcript;
        existing.OriginalTextSnapshot = c.OriginalTextSnapshot;
        existing.UpdatedUtc = now;

        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class DismissAudioReviewHandler(ProjectDbSession session) : ICommandHandler<DismissAudioReviewCommand>
{
    public async Task<Guid?> HandleAsync(DismissAudioReviewCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var existing = await db.AudioReviews
            .FirstOrDefaultAsync(r => r.ParagraphItemId == c.ParagraphItemId, ct);
        if (existing == null) return null;

        existing.State = EntityState.Dismissed;
        existing.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }
}
