using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Characters;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Stamps one item's speaker — any speaker, on any speech item. Narration is a speaker, not an
/// item type (ADR-0006): stamping the narrator sentinel makes the item narration, stamping a
/// character makes it that character's line, and clearing it hands the item back to the
/// attribution queue as unattributed dialog. <c>VoiceResolver</c> honours whatever is
/// stamped, so the flip is heard rather than merely displayed.
/// <para>
/// A hand-flip is an explicit "this is the wrong voice", so it discards the item's generated
/// audio and returns it to the audio queue — otherwise the old voice survives into the exported
/// m4b while the item still counts as complete. <c>AttributeItemsHandler</c> deliberately does
/// not do this; see ADR-0006 for why that asymmetry is left standing.
/// </para>
/// </summary>
public sealed class SetItemCharacterHandler(ProjectDbSession session) : ICommandHandler<SetItemCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetItemCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var item = await db.ParagraphItems.Include(i => i.Character).FirstOrDefaultAsync(i => i.Id == c.ItemId);
        if (item == null) return null;
        // Any speaker on any *speech* item. A pause is nobody's: nothing reads a stamped pause and
        // every reader filters it out, so the stamp would sit there invisible and untrue.
        if (ParagraphItemKinds.IsPause(item.ItemType)) return null;
        if (item.CharacterId == c.CharacterId) return null;
        item.CharacterId = c.CharacterId;
        item.AudioFileName = null;
        item.Character = c.CharacterId.HasValue
            ? await db.Characters.FindAsync(c.CharacterId.Value)
            : null;
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class CreateCharacterHandler(ProjectDbSession session) : ICommandHandler<CreateCharacterCommand>
{
    public async Task<Guid?> HandleAsync(CreateCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var all = await db.Characters.Include(ch => ch.Aliases).ToListAsync(ct);
        var existing = all.FirstOrDefault(ch => CharacterResolver.Matches(ch, c.Name));
        if (existing != null) return existing.Id;
        var character = new Character { Id = Guid.NewGuid(), Name = c.Name };
        db.Characters.Add(character);
        await db.SaveChangesAsync();
        return character.Id;
    }
}

/// <summary>
/// Stamps a speaker across a paragraph, sweeping its speech items *except* the narration —
/// the same line the old <c>ItemType == Character</c> filter drew, now expressed against the
/// speaker (ADR-0006). Preserving narration is what stops a one-gesture speaker fix destroying
/// the paragraph's narration/dialog split. Assigning the narrator is allowed and means "make
/// this paragraph narration"; under the same sweep rule it is idempotent.
/// </summary>
public sealed class SetParagraphCharacterHandler(ProjectDbSession session) : ICommandHandler<SetParagraphCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetParagraphCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var items = await db.ParagraphItems
            .Where(i => i.ParagraphId == c.ParagraphId)
            .Where(NarrationRule.IsDialogExpression)
            .ToListAsync();
        foreach (var item in items)
        {
            // Only an item this gesture actually moves loses its audio; one already on the target
            // speaker keeps what it has, which is what makes assigning the narrator idempotent.
            if (item.CharacterId != c.CharacterId)
            {
                item.CharacterId = c.CharacterId;
                item.AudioFileName = null;
            }
            if (c.CharacterId.HasValue && c.VoiceInstructions != null)
                item.VoiceInstructions = c.VoiceInstructions;
        }
        await db.SaveChangesAsync();
        return null;
    }
}

/// <summary>
/// The bulk sibling of <see cref="SetParagraphCharacterHandler"/>: one set-based update, no
/// entities loaded, so a thousand-paragraph selection costs no change-tracker time. The id list
/// is not chunked — EF translates <c>Contains</c> to <c>IN (SELECT value FROM json_each(@ids))</c>,
/// a single parameter at any length. <c>VoiceInstructions</c> is left alone: there is no
/// per-line instruction to spread across a selection. It sweeps the same non-narrator speech
/// items its sibling does, so narration survives a thousand-paragraph correction.
/// </summary>
public sealed class SetParagraphsCharacterHandler(ProjectDbSession session) : ICommandHandler<SetParagraphsCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetParagraphsCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await db.ParagraphItems
            .Where(i => c.ParagraphIds.Contains(i.ParagraphId))
            .Where(NarrationRule.IsDialogExpression)
            .Where(i => i.CharacterId != c.CharacterId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.CharacterId, c.CharacterId)
                .SetProperty(i => i.AudioFileName, (string?)null), ct);
        return null;
    }
}

public sealed class AddCharacterAliasHandler(ProjectDbSession session) : ICommandHandler<AddCharacterAliasCommand>
{
    public async Task<Guid?> HandleAsync(AddCharacterAliasCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var character = await db.Characters
            .Include(ch => ch.Aliases)
            .FirstOrDefaultAsync(ch => ch.Id == c.CharacterId);
        if (character == null) return null;

        var alreadyExists =
            string.Equals(character.Name, c.Name, StringComparison.OrdinalIgnoreCase) ||
            character.Aliases.Any(a => string.Equals(a.Name, c.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyExists) return null;

        db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = c.CharacterId, Name = c.Name });
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class RemoveCharacterAliasHandler(ProjectDbSession session) : ICommandHandler<RemoveCharacterAliasCommand>
{
    public async Task<Guid?> HandleAsync(RemoveCharacterAliasCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var alias = await db.CharacterAliases.FindAsync(c.AliasId);
        if (alias == null) return null;
        db.CharacterAliases.Remove(alias);
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class MergeCharactersHandler(ProjectDbSession session) : ICommandHandler<MergeCharactersCommand>
{
    public async Task<Guid?> HandleAsync(MergeCharactersCommand c, CancellationToken ct)
    {
        if (c.MergedId == ProjectDbContext.NarratorId || c.SurvivorId == ProjectDbContext.NarratorId) return null;

        var db = await session.OpenAsync(c.FolderId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var merged = await db.Characters.Include(ch => ch.Aliases).FirstOrDefaultAsync(ch => ch.Id == c.MergedId, ct);
        if (merged == null) { await tx.RollbackAsync(ct); return null; }

        await db.ParagraphItems
            .Where(i => i.CharacterId == c.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, c.SurvivorId), ct);

        await db.CharacterAliases
            .Where(a => a.CharacterId == c.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CharacterId, c.SurvivorId), ct);

        // The merged character's voices die with it — the survivor keeps its own. Rules and voices
        // must go explicitly, in this order: the DB cascades Character→Voice, but VoiceRules.VoiceId
        // is Restrict, so leaving them to the cascade makes the character delete below fail on the FK
        // and rolls the whole merge back. Same shape as DeleteCharacterHandler, including the rule
        // owned by a *different* character that points at one of these voices.
        var mergedVoiceIds = await db.Voices
            .Where(v => v.CharacterId == c.MergedId)
            .Select(v => v.Id)
            .ToListAsync(ct);

        await db.VoiceRules
            .Where(r => r.CharacterId == c.MergedId || mergedVoiceIds.Contains(r.VoiceId))
            .ExecuteDeleteAsync(ct);

        await db.Voices
            .Where(v => v.CharacterId == c.MergedId)
            .ExecuteDeleteAsync(ct);

        if (c.AddNameAsAlias)
        {
            var survivorAliasNames = await db.CharacterAliases
                .Where(a => a.CharacterId == c.SurvivorId)
                .Select(a => a.Name.ToLower())
                .ToListAsync(ct);
            var survivorNameLower = (await db.Characters.Where(ch => ch.Id == c.SurvivorId).Select(ch => ch.Name).FirstAsync(ct)).ToLower();

            void AddIfAbsent(string name)
            {
                if (!string.Equals(survivorNameLower, name, StringComparison.OrdinalIgnoreCase) &&
                    !survivorAliasNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                {
                    db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = c.SurvivorId, Name = name });
                    survivorAliasNames.Add(name.ToLower());
                }
            }

            AddIfAbsent(merged.Name);
            foreach (var alias in merged.Aliases)
                AddIfAbsent(alias.Name);

            await db.SaveChangesAsync(ct);
        }

        // A merge says the two are the same person, so the narrator link follows the survivor —
        // silently, no warning. Linked-on-the-survivor-side is a no-op: the Where matches nothing.
        await db.Projects
            .Where(p => p.NarratorCharacterId == c.MergedId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.NarratorCharacterId, (Guid?)c.SurvivorId), ct);

        await db.Characters
            .Where(ch => ch.Id == c.MergedId)
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return null;
    }
}

public sealed class RenameCharacterHandler(ProjectDbSession session) : ICommandHandler<RenameCharacterCommand>
{
    public async Task<Guid?> HandleAsync(RenameCharacterCommand c, CancellationToken ct)
    {
        if (c.CharacterId == ProjectDbContext.NarratorId) return null;

        var db = await session.OpenAsync(c.FolderId);
        var character = await db.Characters.FindAsync([c.CharacterId], ct);
        if (character == null) return null;

        character.Name = c.Name;
        await db.SaveChangesAsync(ct);
        return null;
    }
}

public sealed class DeleteCharacterHandler(ProjectDbSession session) : ICommandHandler<DeleteCharacterCommand>
{
    public async Task<Guid?> HandleAsync(DeleteCharacterCommand c, CancellationToken ct)
    {
        if (c.CharacterId == ProjectDbContext.NarratorId) return null;

        var db = await session.OpenAsync(c.FolderId);
        if (!await db.Characters.AnyAsync(ch => ch.Id == c.CharacterId, ct)) return null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.ParagraphItems
            .Where(i => i.CharacterId == c.CharacterId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, (Guid?)null), ct);

        await db.CharacterAliases
            .Where(a => a.CharacterId == c.CharacterId)
            .ExecuteDeleteAsync(ct);

        // VoiceRules.VoiceId is Restrict, so the DB's Character→Voice cascade cannot fire while any
        // rule still points at one of this character's voices — including a rule owned by a different
        // character. Clear the rules first, then the voices, then the character.
        var voiceIds = await db.Voices
            .Where(v => v.CharacterId == c.CharacterId)
            .Select(v => v.Id)
            .ToListAsync(ct);

        await db.VoiceRules
            .Where(r => r.CharacterId == c.CharacterId || voiceIds.Contains(r.VoiceId))
            .ExecuteDeleteAsync(ct);

        await db.Voices
            .Where(v => v.CharacterId == c.CharacterId)
            .ExecuteDeleteAsync(ct);

        // Deleting the character a book narrates with unlinks it, inside this transaction so the
        // delete and the unlink cannot half-land. Not a rejection: this handler's only error
        // channel is a silent `return null`, which reads as a delete that didn't happen.
        // NarratorIdentity would still survive the dangling pointer — that fallback is the
        // backstop, not the fix. Set-based like the rest of this transaction, so a Project entity
        // tracked earlier in the same session keeps the old id; harmless while the seam reads
        // through a projection (ADR-0004), and the reason it must keep doing so.
        await db.Projects
            .Where(p => p.NarratorCharacterId == c.CharacterId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.NarratorCharacterId, (Guid?)null), ct);

        await db.Characters
            .Where(ch => ch.Id == c.CharacterId)
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return null;
    }
}
