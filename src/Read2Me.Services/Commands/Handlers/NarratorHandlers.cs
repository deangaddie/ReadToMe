using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The single write path for the narrator link — the agent API and the book banner both land
/// here, so human and machine paths cannot drift (ADR-0004).
/// </summary>
/// <remarks>
/// Deliberately breaks the sibling handlers' <c>return null</c> house style: a rejected write
/// throws, and <c>CommandEndpoints</c> turns that into a 422. Returning null would render
/// rejection to an agent as <c>200 { "newEntityId": null }</c>, indistinguishable from success.
/// Only the write side rejects a self-link; <see cref="NarratorIdentity"/> stays permissive so a
/// link written before this guard existed still resolves rather than failing a whole book's audio.
/// </remarks>
public sealed class SetNarratorCharacterHandler(ProjectDbSession session)
    : ICommandHandler<SetNarratorCharacterCommand>
{
    public async Task<Guid?> HandleAsync(SetNarratorCharacterCommand c, CancellationToken ct)
    {
        if (c.CharacterId == ProjectDbContext.NarratorId)
            throw new InvalidOperationException(
                "The seed Narrator row cannot narrate itself — that is the unlinked state. Send null to unlink.");

        var db = await session.OpenAsync(c.FolderId);

        if (c.CharacterId is { } id && !await db.Characters.AnyAsync(ch => ch.Id == id, ct))
            throw new InvalidOperationException($"No character '{id}' in this project.");

        var project = await db.Projects.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("This project has no project row to set a narrator on.");

        project.NarratorCharacterId = c.CharacterId;
        await db.SaveChangesAsync(ct);
        return null;
    }
}
