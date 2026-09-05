using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Characters;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the Character, narrator and policy family shares (ADR 0007). None of these mutations names
/// a Paragraph, and almost all of them change what every Paragraph means: a merge moves every line
/// the merged Character spoke, a delete hands its lines back to the attribution queue, the narrator
/// link decides whose Voice narration is read in, and NarratorOnlyMode decides which items are
/// audio-eligible at all.
/// <para>
/// So the effects here are deliberately not the exact, row-scoped kind the attribution and audio
/// families report. Naming rows a reader could refresh in place would be a lie about a change whose
/// reach is the whole Book — the facets say <see cref="BookFacets.Characters"/>,
/// <see cref="BookFacets.Narrator"/> or <see cref="BookFacets.ProjectPolicy"/>, none of which a
/// Book View can place on one row, so every one of them reconciles by rebuilding.
/// </para>
/// <para>
/// The seed Narrator row is protected throughout. It is not a Character anyone invented: it is the
/// unlinked state of narration itself (ADR-0004), so renaming, deleting or merging it is a refusal
/// rather than a no-op.
/// </para>
/// </summary>
internal static class RosterEffects
{
    /// <summary>A roster change that moved no line: nothing on any Paragraph to name, and none to hide.</summary>
    public static BookMutationEffects Roster(BookFacets facets, Guid? createdId = null) => new()
    {
        Scope = BookMutationScope.Exact,
        Facets = facets,
        CreatedId = createdId,
    };

    /// <summary>
    /// A change whose reach is the Book: it restamped, unlinked or re-scoped items this mutation
    /// deliberately did not enumerate, so the scope says so rather than naming a subset.
    /// </summary>
    public static BookMutationEffects BookWide(BookFacets facets) => new()
    {
        Scope = BookMutationScope.WholeProject,
        Facets = facets,
    };

    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} in this project.");

    public static BookMutationRejectedException ProtectedNarrator(string verb) =>
        new(BookMutationRejection.Validation,
            $"The seed Narrator row cannot be {verb} — it is the unlinked state of narration, not a character.");

    /// <summary>
    /// Clears the Voice Rules that would otherwise outlive a Character's Voices.
    /// <c>VoiceRules.VoiceId</c> is Restrict, so the database's Character-to-Voice cascade cannot
    /// fire while any rule still points at one of them — including a rule owned by a
    /// <em>different</em> Character. Rules first, then Voices, then the Character.
    /// </summary>
    public static async Task<(int Rules, int Voices)> RemoveVoicesAndRulesAsync(
        ProjectDbContext db, Guid characterId, CancellationToken ct)
    {
        var voiceIds = await db.Voices
            .Where(v => v.CharacterId == characterId)
            .Select(v => v.Id)
            .ToListAsync(ct);

        var rules = await db.VoiceRules
            .Where(r => r.CharacterId == characterId || voiceIds.Contains(r.VoiceId))
            .ExecuteDeleteAsync(ct);

        var voices = await db.Voices
            .Where(v => v.CharacterId == characterId)
            .ExecuteDeleteAsync(ct);

        return (rules, voices);
    }
}

/// <summary>
/// Creates a Character — unless the roster already answers to that name. Matching is by canonical
/// name <em>or</em> alias, case-insensitively, because the roster is keyed by what a speaker is
/// called: a name that already resolves to somebody is not a second somebody.
/// <para>
/// So an existing match is a valid gesture that changes nothing, and the receipt carries no created
/// id. A producer that needs the id of whoever answers to a name resolves it by reading, which is
/// what <see cref="CharacterResolver.ResolveOrCreateAsync"/> is for.
/// </para>
/// </summary>
public sealed class CreateCharacterMutationImplementation : IBookMutationImplementation<CreateCharacterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        CreateCharacterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var roster = await db.Characters.Include(c => c.Aliases).ToListAsync(ct);
        if (roster.Any(c => CharacterResolver.Matches(c, mutation.CharacterName)))
            return BookMutationEffects.Nothing;

        var character = new Character { Id = Guid.NewGuid(), Name = mutation.CharacterName };
        db.Characters.Add(character);

        return RosterEffects.Roster(BookFacets.Characters, character.Id);
    }
}

/// <summary>
/// Renames a Character, leaving its aliases, Voices and lines where they are. A rename to the name
/// it already has changes nothing; a rename that only changes case does not, which is why the
/// comparison here is ordinal while <em>matching</em> a speaker by name is not.
/// </summary>
public sealed class RenameCharacterMutationImplementation : IBookMutationImplementation<RenameCharacterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        RenameCharacterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (mutation.CharacterId == ProjectDbContext.NarratorId)
            throw RosterEffects.ProtectedNarrator("renamed");

        var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == mutation.CharacterId, ct)
            ?? throw RosterEffects.NotFound("character", mutation.CharacterId);

        if (string.Equals(character.Name, mutation.CharacterName, StringComparison.Ordinal))
            return BookMutationEffects.Nothing;

        character.Name = mutation.CharacterName;
        return RosterEffects.Roster(BookFacets.Characters);
    }
}

/// <summary>
/// Gives a Character another name it answers to. A name it already answers to — its own, or an alias
/// it already carries — changes nothing, which is what makes re-applying a discovery result safe.
/// </summary>
public sealed class AddCharacterAliasMutationImplementation : IBookMutationImplementation<AddCharacterAliasMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AddCharacterAliasMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var character = await db.Characters
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == mutation.CharacterId, ct)
            ?? throw RosterEffects.NotFound("character", mutation.CharacterId);

        if (CharacterResolver.Matches(character, mutation.AliasName))
            return BookMutationEffects.Nothing;

        db.CharacterAliases.Add(new CharacterAlias
        {
            Id = Guid.NewGuid(),
            CharacterId = mutation.CharacterId,
            Name = mutation.AliasName,
        });

        return RosterEffects.Roster(BookFacets.Characters);
    }
}

/// <summary>Takes one alias away from whichever Character owns it.</summary>
public sealed class RemoveCharacterAliasMutationImplementation
    : IBookMutationImplementation<RemoveCharacterAliasMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        RemoveCharacterAliasMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var alias = await db.CharacterAliases.FirstOrDefaultAsync(a => a.Id == mutation.AliasId, ct)
            ?? throw RosterEffects.NotFound("character alias", mutation.AliasId);

        db.CharacterAliases.Remove(alias);
        return RosterEffects.Roster(BookFacets.Characters);
    }
}

/// <summary>
/// Declares two Characters to be one person. Every line the merged Character spoke becomes the
/// survivor's and its aliases move across, while its Voices and Voice Rules die with it — the
/// survivor keeps its own, because a Voice is a recording of one person and this gesture says the
/// recordings were never of two.
/// <para>
/// A narrator link on the merged side follows the survivor silently: the merge has just said they
/// are the same person, so there is nothing to warn about.
/// </para>
/// <para>
/// Merging a Character into itself is refused rather than applied. The steps below would repoint its
/// lines at it and then delete it, destroying a Character and its lines' attribution in answer to a
/// gesture that plainly meant nothing.
/// </para>
/// </summary>
public sealed class MergeCharactersMutationImplementation : IBookMutationImplementation<MergeCharactersMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergeCharactersMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (mutation.MergedId == ProjectDbContext.NarratorId || mutation.SurvivorId == ProjectDbContext.NarratorId)
            throw RosterEffects.ProtectedNarrator("merged");

        if (mutation.MergedId == mutation.SurvivorId)
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation, "A character cannot be merged into itself.");

        // Loaded with its aliases before anything moves: the alias rows are repointed set-based
        // below, which the change tracker never sees, so this instance keeps the names the
        // add-as-alias step needs.
        var merged = await db.Characters
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == mutation.MergedId, ct)
            ?? throw RosterEffects.NotFound("character", mutation.MergedId);

        var survivorName = await db.Characters
            .Where(c => c.Id == mutation.SurvivorId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct)
            ?? throw RosterEffects.NotFound("character", mutation.SurvivorId);

        var lines = await db.ParagraphItems
            .Where(i => i.CharacterId == mutation.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, mutation.SurvivorId), ct);

        await db.CharacterAliases
            .Where(a => a.CharacterId == mutation.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CharacterId, mutation.SurvivorId), ct);

        var (rules, voices) = await RosterEffects.RemoveVoicesAndRulesAsync(db, mutation.MergedId, ct);

        if (mutation.AddNameAsAlias)
            await AddNamesAsAliasesAsync(db, mutation.SurvivorId, survivorName, merged, ct);

        // A no-op when the survivor is the linked one: the Where matches nothing.
        var narrator = await db.Projects
            .Where(p => p.NarratorCharacterId == mutation.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.NarratorCharacterId, (Guid?)mutation.SurvivorId), ct);

        await db.Characters
            .Where(c => c.Id == mutation.MergedId)
            .ExecuteDeleteAsync(ct);

        return RosterEffects.BookWide(RosterFacets(lines, voices, rules, narrator));
    }

    /// <summary>
    /// Keeps the merged Character findable under every name it had: its own, plus each alias that
    /// moved. Names the survivor already answers to are skipped, so a merge cannot create a duplicate
    /// alias that would make one string resolve twice.
    /// </summary>
    private static async Task AddNamesAsAliasesAsync(
        ProjectDbContext db, Guid survivorId, string survivorName, Character merged, CancellationToken ct)
    {
        var taken = await db.CharacterAliases
            .Where(a => a.CharacterId == survivorId)
            .Select(a => a.Name)
            .ToListAsync(ct);
        taken.Add(survivorName);

        foreach (var name in merged.Aliases.Select(a => a.Name).Prepend(merged.Name))
        {
            if (taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))) continue;

            db.CharacterAliases.Add(new CharacterAlias
            {
                Id = Guid.NewGuid(),
                CharacterId = survivorId,
                Name = name,
            });
            taken.Add(name);
        }
    }

    /// <summary>
    /// The facets a roster removal actually moved, from the row counts its steps reported. The
    /// Character itself always moves; whether any line, Voice, Voice Rule or narrator link went with
    /// it is a fact about this Book, not about the gesture.
    /// </summary>
    internal static BookFacets RosterFacets(int lines, int voices, int rules, int narratorLinks)
    {
        var facets = BookFacets.Characters;
        if (lines > 0) facets |= BookFacets.Attribution;
        if (voices > 0) facets |= BookFacets.Voices;
        if (rules > 0) facets |= BookFacets.VoiceRules;
        if (narratorLinks > 0) facets |= BookFacets.Narrator;
        return facets;
    }
}

/// <summary>
/// Removes a Character. Its lines survive as unattributed dialog for the attribution queue to answer
/// again — the text was still spoken, only by nobody the roster now knows — while its aliases,
/// Voices and Voice Rules go with it.
/// <para>
/// A narrator link to it is cleared inside the same transaction, so the delete and the unlink cannot
/// half-land. <see cref="NarratorIdentity"/> would survive the dangling pointer, but that fallback is
/// the backstop, not the fix.
/// </para>
/// </summary>
public sealed class DeleteCharacterMutationImplementation : IBookMutationImplementation<DeleteCharacterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteCharacterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (mutation.CharacterId == ProjectDbContext.NarratorId)
            throw RosterEffects.ProtectedNarrator("deleted");

        if (!await db.Characters.AnyAsync(c => c.Id == mutation.CharacterId, ct))
            throw RosterEffects.NotFound("character", mutation.CharacterId);

        var lines = await db.ParagraphItems
            .Where(i => i.CharacterId == mutation.CharacterId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, (Guid?)null), ct);

        await db.CharacterAliases
            .Where(a => a.CharacterId == mutation.CharacterId)
            .ExecuteDeleteAsync(ct);

        var (rules, voices) = await RosterEffects.RemoveVoicesAndRulesAsync(db, mutation.CharacterId, ct);

        // Set-based like the rest, so a Project entity tracked earlier in the same session keeps the
        // old id; harmless while the narrator seam reads through a projection (ADR-0004), and the
        // reason it must keep doing so.
        var narrator = await db.Projects
            .Where(p => p.NarratorCharacterId == mutation.CharacterId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.NarratorCharacterId, (Guid?)null), ct);

        await db.Characters
            .Where(c => c.Id == mutation.CharacterId)
            .ExecuteDeleteAsync(ct);

        return RosterEffects.BookWide(
            MergeCharactersMutationImplementation.RosterFacets(lines, voices, rules, narrator));
    }
}

/// <summary>
/// Says which Character narrates this Book, or unlinks narration from the roster with null
/// (ADR-0004).
/// <para>
/// Two refusals, both explicit rather than silent, because this is the one write in the family a
/// machine caller drives: the seed Narrator row cannot narrate itself — that <em>is</em> the
/// unlinked state — and a link to a Character this project does not have is a mistake, not a no-op.
/// Only the write side is this strict; <see cref="NarratorIdentity"/> stays permissive so a link
/// written before the guard existed still resolves rather than failing a whole Book's audio.
/// </para>
/// </summary>
public sealed class SetNarratorCharacterMutationImplementation
    : IBookMutationImplementation<SetNarratorCharacterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetNarratorCharacterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (mutation.CharacterId == ProjectDbContext.NarratorId)
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                "The seed Narrator row cannot narrate itself — that is the unlinked state. Send null to unlink.");

        if (mutation.CharacterId is { } id && !await db.Characters.AnyAsync(c => c.Id == id, ct))
            throw RosterEffects.NotFound("character", id);

        var project = await db.Projects.FirstOrDefaultAsync(ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound, "This project has no project row to set a narrator on.");

        if (project.NarratorCharacterId == mutation.CharacterId)
            return BookMutationEffects.Nothing;

        project.NarratorCharacterId = mutation.CharacterId;
        return RosterEffects.BookWide(BookFacets.Narrator);
    }
}

/// <summary>
/// Turns the Book-wide narrator-only policy on or off. It writes one column on one row and still
/// changes what every item <em>is</em>: which items the Audio Queue may speak, and with them the
/// audio denominators and the Audio Item Selection's eligibility. Whole-project scope is the honest
/// answer here, not a conservative one.
/// </summary>
public sealed class SetNarratorOnlyModeMutationImplementation
    : IBookMutationImplementation<SetNarratorOnlyModeMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetNarratorOnlyModeMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                "This project has no project row to set narrator-only mode on.");

        if (project.NarratorOnlyMode == mutation.Enabled)
            return BookMutationEffects.Nothing;

        project.NarratorOnlyMode = mutation.Enabled;
        return RosterEffects.BookWide(BookFacets.ProjectPolicy);
    }
}
