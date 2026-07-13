using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Characters;

namespace Read2Me.Services.Commands.Handlers;

public sealed class SetItemCharacterHandler(ProjectDbSession session) : ICommandHandler<SetItemCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetItemCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var item = await db.ParagraphItems.Include(i => i.Character).FirstOrDefaultAsync(i => i.Id == c.ItemId);
        if (item == null) return null;
        item.CharacterId = c.CharacterId;
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

public sealed class SetParagraphCharacterHandler(ProjectDbSession session) : ICommandHandler<SetParagraphCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetParagraphCharacterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var items = await db.ParagraphItems
            .Where(i => i.ParagraphId == c.ParagraphId && i.ItemType == ParagraphItemType.Character)
            .ToListAsync();
        foreach (var item in items)
        {
            item.CharacterId = c.CharacterId;
            if (c.CharacterId.HasValue && c.VoiceInstructions != null)
                item.VoiceInstructions = c.VoiceInstructions;
        }
        await db.SaveChangesAsync();
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

        await db.Characters
            .Where(ch => ch.Id == c.CharacterId)
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return null;
    }
}
