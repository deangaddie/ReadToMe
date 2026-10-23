using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Services.Pronunciations;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Adds, edits or deletes the entry, and in the same commit discards the audio of every item that has
/// audio and whose Spoken text the change alters (<see cref="DraftedList.ChangedSpokenText"/>), through
/// <see cref="BookEditEffects.DiscardAudioAsync"/> as a text edit does. Items without audio change
/// silently. An edit that leaves both forms as they are writes nothing.
/// </summary>
public sealed class SetPronunciationMutationImplementation : IBookMutationImplementation<SetPronunciationMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetPronunciationMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var drafted = mutation.Draft.ApplyTo(await db.Pronunciations.AsNoTracking().ToListAsync(ct));
        if (drafted.DuplicateOf is { } duplicate)
            throw new BookMutationRejectedException(BookMutationRejection.Conflict,
                $"\"{drafted.Entry.WrittenForm}\" already has a pronunciation: \"{duplicate.WrittenForm}\".");
        if (drafted.Unchanged)
            return BookMutationEffects.Nothing;

        if (drafted.Previous is null)
            db.Pronunciations.Add(drafted.Entry);
        else
        {
            var stored = await db.Pronunciations.SingleAsync(p => p.Id == drafted.Previous.Id, ct);
            if (drafted.Deleted)
                db.Pronunciations.Remove(stored);
            else
                (stored.WrittenForm, stored.SpokenForm) = (drafted.Entry.WrittenForm, drafted.Entry.SpokenForm);
        }

        var withAudio = await db.ParagraphItems.AsNoTracking()
            .Where(i => i.AudioFileName != null && i.AudioFileName != "" && i.Text != null)
            .Select(i => new { i.Id, i.Text })
            .ToListAsync(ct);
        var changed = drafted.ChangedSpokenText(withAudio.Select(i => (i.Id, i.Text!)));
        var items = await db.ParagraphItems.Where(i => changed.Contains(i.Id)).ToListAsync(ct);
        var facets = BookFacets.Pronunciations;
        foreach (var item in items)
            facets |= await BookEditEffects.DiscardAudioAsync(db, item, ct);

        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact,
            Facets = facets,
            CreatedId = drafted.Previous is null ? drafted.Entry.Id : null,
            ParagraphIds = [.. items.Select(i => i.ParagraphId).Distinct()],
            ParagraphItemIds = [.. items.Select(i => i.Id)],
        };
    }
}
