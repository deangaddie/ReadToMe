using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Pronunciations;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Adds the entry with its forms normalised, unless one is invalid (<see cref="BookMutationRejection.Validation"/>)
/// or its written form is already listed, ignoring case.
/// </summary>
public sealed class AddPronunciationMutationImplementation : IBookMutationImplementation<AddPronunciationMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddPronunciationMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var writtenForm = PronunciationForms.Normalise(mutation.WrittenForm);
        var spokenForm = PronunciationForms.Normalise(mutation.SpokenForm);
        if ((PronunciationForms.WrittenProblem(writtenForm) ?? PronunciationForms.SpokenProblem(spokenForm)) is { } problem)
            throw new BookMutationRejectedException(BookMutationRejection.Validation, problem);

        // In memory, not in SQL: SQLite's NOCASE folds ASCII only, and the list is small.
        var listed = await db.Pronunciations.Select(p => p.WrittenForm).ToListAsync(ct);
        if (listed.Any(w => string.Equals(w, writtenForm, StringComparison.OrdinalIgnoreCase)))
            throw new BookMutationRejectedException(BookMutationRejection.Conflict,
                $"\"{writtenForm}\" already has a pronunciation.");

        var entry = new Pronunciation { Id = Guid.NewGuid(), WrittenForm = writtenForm, SpokenForm = spokenForm };
        db.Pronunciations.Add(entry);
        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact,
            Facets = BookFacets.Pronunciations,
            CreatedId = entry.Id,
        };
    }
}
