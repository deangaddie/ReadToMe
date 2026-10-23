using Read2Me.Data.Entities;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Pronunciations;

/// <summary>
/// A change to the Pronunciation list: an add (no <paramref name="Id"/>), an edit of entry
/// <paramref name="Id"/>, or its delete. The forms are ignored on a delete. The impact query and
/// the mutation both draft the list from one of these, so what the query counts is what a save does.
/// </summary>
public sealed record PronunciationDraft(Guid? Id, string? WrittenForm, string? SpokenForm, bool Delete = false)
{
    /// <summary>
    /// The list before and after this change. An invalid form is a <see cref="BookMutationRejection.Validation"/>
    /// and an unknown id a <see cref="BookMutationRejection.NotFound"/>, both thrown; a duplicate written
    /// form is reported, not thrown, so the impact query can show it.
    /// </summary>
    public DraftedList ApplyTo(IReadOnlyList<Pronunciation> list)
    {
        var previous = Id is { } id
            ? list.FirstOrDefault(p => p.Id == id)
              ?? throw new BookMutationRejectedException(BookMutationRejection.NotFound, $"No pronunciation {id}.")
            : null;
        if (Delete)
            return previous is null
                ? throw new BookMutationRejectedException(BookMutationRejection.Validation, "A delete names the entry it deletes.")
                : new DraftedList(list, [.. list.Where(p => p != previous)], previous, previous, null, Deleted: true);

        var writtenForm = PronunciationForms.Normalise(WrittenForm);
        var spokenForm = PronunciationForms.Normalise(SpokenForm);
        if ((PronunciationForms.WrittenProblem(writtenForm) ?? PronunciationForms.SpokenProblem(spokenForm)) is { } problem)
            throw new BookMutationRejectedException(BookMutationRejection.Validation, problem);

        var entry = new Pronunciation { Id = previous?.Id ?? Guid.NewGuid(), WrittenForm = writtenForm, SpokenForm = spokenForm };
        // Ignoring case in memory, not in SQL: SQLite's NOCASE folds ASCII only, and the list is small.
        var duplicate = list.FirstOrDefault(p => p != previous
            && string.Equals(p.WrittenForm, writtenForm, StringComparison.OrdinalIgnoreCase));
        return new DraftedList(list, [.. list.Where(p => p != previous && p != duplicate), entry], entry, previous, duplicate, Deleted: false);
    }
}

/// <summary>The list before and after a <see cref="PronunciationDraft"/>.</summary>
/// <param name="Entry">The entry as the draft leaves it; for a delete, the entry deleted.</param>
/// <param name="Previous">The entry as it was, or null for an add.</param>
/// <param name="DuplicateOf">
/// Another entry with the draft's written form, ignoring case. A save refuses it; the impact query
/// reports it and counts the draft as if it took that entry's place, so <see cref="After"/> leaves it out.
/// </param>
public sealed record DraftedList(
    IReadOnlyList<Pronunciation> Before, IReadOnlyList<Pronunciation> After,
    Pronunciation Entry, Pronunciation? Previous, Pronunciation? DuplicateOf, bool Deleted)
{
    /// <summary>The list <see cref="Entry"/> is in: after the change, or before it for a delete.</summary>
    public IReadOnlyList<Pronunciation> WithEntry => Deleted ? Before : After;

    /// <summary>An edit that leaves both forms as they are: saving it writes nothing.</summary>
    public bool Unchanged => !Deleted && Previous is not null
        && Previous.WrittenForm == Entry.WrittenForm && Previous.SpokenForm == Entry.SpokenForm;

    /// <summary>
    /// The items, of those given, whose Spoken text the change alters. Only an item holding a word of the
    /// entry's old or new written form can change — a new match, a lost one or an overlap whose winner
    /// shifts all start on one — so only those are respelt twice to compare.
    /// </summary>
    public IReadOnlyList<Guid> ChangedSpokenText(IEnumerable<(Guid Id, string Text)> items)
    {
        if (Unchanged)
            return [];
        var firstWords = new[] { Previous?.WrittenForm, Entry.WrittenForm }
            .OfType<string>().Select(w => w.Split(' ')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = new PronunciationMatcher(Before);
        var after = new PronunciationMatcher(After);
        return [.. items
            .Where(i => firstWords.Any(w => i.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Where(i => before.SpokenTextOf(i.Text) != after.SpokenTextOf(i.Text))
            .Select(i => i.Id)];
    }
}
