using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using EntityReviewState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the audio assignment and review family shares (ADR 0007). Every mutation here names one
/// existing ParagraphItem and changes only data hanging off it — its audio reference, its review
/// row — so each reports the exact Paragraph and item a Book View has to reread.
/// <para>
/// That exactness matters for the same reason it does in speaker attribution: the Audio Queue
/// commits once per item, and a reader that rebuilt its expanded branches for each take would spend
/// a queue run rereading a Book that gained one WAV.
/// </para>
/// </summary>
internal static class AudioEffects
{
    public static BookMutationEffects Recorded(Guid paragraphId, Guid itemId, BookFacets facets) => new()
    {
        Scope = BookMutationScope.Exact,
        Facets = facets,
        ParagraphIds = [paragraphId],
        ParagraphItemIds = [itemId],
    };

    /// <summary>The item, or an expected rejection — every mutation here needs its Paragraph for the receipt.</summary>
    public static async Task<ParagraphItem> ItemAsync(ProjectDbContext db, Guid itemId, CancellationToken ct) =>
        await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound, $"No paragraph item {itemId} to record audio against.");

    /// <summary>
    /// Applies a verdict to the item's review row and reports whether it moved.
    /// <para>
    /// Row presence is the whole signal: a row exists if and only if a stage failed, so a clean take
    /// deletes one rather than storing a passing verdict. A fresh failure always returns the row to
    /// needs-review, which is what makes a dismissal cover the take it was given and not the next
    /// one.
    /// </para>
    /// <para>
    /// A verdict identical to the one already recorded changes nothing and says so. The legacy
    /// handler restamped <c>UpdatedUtc</c> unconditionally, which made every re-record of an
    /// unchanged failure a write for readers to reconcile.
    /// </para>
    /// </summary>
    public static async Task<bool> ApplyVerdictAsync(
        ProjectDbContext db, Guid itemId, AudioReviewVerdict verdict, CancellationToken ct)
    {
        var existing = await db.AudioReviews.FirstOrDefaultAsync(r => r.ParagraphItemId == itemId, ct);

        if (verdict.NormalizeOk && verdict.VerifyOk)
        {
            if (existing is null) return false;
            db.AudioReviews.Remove(existing);
            return true;
        }

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            db.AudioReviews.Add(Stamp(
                new AudioReview { Id = Guid.NewGuid(), ParagraphItemId = itemId, CreatedUtc = now },
                verdict, now));
            return true;
        }

        if (!Differs(existing, verdict)) return false;

        Stamp(existing, verdict, now);
        return true;
    }

    private static bool Differs(AudioReview row, AudioReviewVerdict verdict) =>
        row.State != EntityReviewState.NeedsReview
        || row.NormalizeOk != verdict.NormalizeOk
        || row.NormalizeReason != verdict.NormalizeReason
        || row.VerifyOk != verdict.VerifyOk
        || row.Wer != verdict.Wer
        || row.VerifyReason != verdict.VerifyReason
        || row.Transcript != verdict.Transcript
        || row.OriginalTextSnapshot != verdict.OriginalTextSnapshot;

    private static AudioReview Stamp(AudioReview row, AudioReviewVerdict verdict, DateTime now)
    {
        row.State = EntityReviewState.NeedsReview;
        row.NormalizeOk = verdict.NormalizeOk;
        row.NormalizeReason = verdict.NormalizeReason;
        row.VerifyOk = verdict.VerifyOk;
        row.Wer = verdict.Wer;
        row.VerifyReason = verdict.VerifyReason;
        row.Transcript = verdict.Transcript;
        row.OriginalTextSnapshot = verdict.OriginalTextSnapshot;
        row.UpdatedUtc = now;
        return row;
    }
}

/// <summary>
/// Records a generated take against its item: the audio reference and the verdict on it in one
/// transaction, so no reader can ever see a row playing new audio under the previous take's review
/// chip.
/// <para>
/// This is the one mutation here that never reports no-change. Re-recording an item can leave every
/// column exactly as it was — same path, same clean verdict — and still be a real change, because
/// the file behind the path is a different take. The path is a name, not the artifact, so the
/// <see cref="BookFacets.Audio"/> facet is reported from the fact that audio was recorded rather
/// than from whether a string moved.
/// </para>
/// </summary>
public sealed class RecordParagraphItemAudioMutationImplementation
    : IBookMutationImplementation<RecordParagraphItemAudioMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        RecordParagraphItemAudioMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await AudioEffects.ItemAsync(db, mutation.ItemId, ct);
        item.AudioFileName = mutation.RelativePath;

        var reviewMoved = await AudioEffects.ApplyVerdictAsync(db, mutation.ItemId, mutation.Verdict, ct);

        return AudioEffects.Recorded(item.ParagraphId, item.Id,
            reviewMoved ? BookFacets.Audio | BookFacets.Reviews : BookFacets.Audio);
    }
}

/// <summary>
/// Points an item at an audio file and nothing else. Unlike recording a take this produces no
/// artifact of its own — the caller is naming a file that already exists — so naming the file the
/// item already names changes nothing.
/// </summary>
public sealed class SetParagraphItemAudioMutationImplementation
    : IBookMutationImplementation<SetParagraphItemAudioMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetParagraphItemAudioMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await AudioEffects.ItemAsync(db, mutation.ItemId, ct);
        if (item.AudioFileName == mutation.RelativePath) return BookMutationEffects.Nothing;

        item.AudioFileName = mutation.RelativePath;
        return AudioEffects.Recorded(item.ParagraphId, item.Id, BookFacets.Audio);
    }
}

/// <summary>Records a verdict on audio someone else recorded, leaving the audio reference alone.</summary>
public sealed class SetAudioReviewMutationImplementation
    : IBookMutationImplementation<SetAudioReviewMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetAudioReviewMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await AudioEffects.ItemAsync(db, mutation.ItemId, ct);

        return await AudioEffects.ApplyVerdictAsync(db, mutation.ItemId, mutation.Verdict, ct)
            ? AudioEffects.Recorded(item.ParagraphId, item.Id, BookFacets.Reviews)
            : BookMutationEffects.Nothing;
    }
}

/// <summary>
/// Silences an item's review without regenerating its audio. An item with no review to dismiss —
/// and one already dismissed — is a legal gesture that changes nothing, which is the answer the
/// legacy handler gave by returning null.
/// </summary>
public sealed class DismissAudioReviewMutationImplementation
    : IBookMutationImplementation<DismissAudioReviewMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DismissAudioReviewMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await AudioEffects.ItemAsync(db, mutation.ItemId, ct);

        var existing = await db.AudioReviews.FirstOrDefaultAsync(r => r.ParagraphItemId == mutation.ItemId, ct);
        if (existing is null || existing.State == EntityReviewState.Dismissed)
            return BookMutationEffects.Nothing;

        existing.State = EntityReviewState.Dismissed;
        existing.UpdatedUtc = DateTime.UtcNow;

        return AudioEffects.Recorded(item.ParagraphId, item.Id, BookFacets.Reviews);
    }
}
