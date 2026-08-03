using Microsoft.EntityFrameworkCore;

namespace Read2Me.Data
{
    /// <summary>
    /// Read-time projection of the narrator link (<c>Projects.NarratorCharacterId</c>).
    /// Narration items keep stamping <see cref="ProjectDbContext.NarratorId"/> forever;
    /// this type resolves who that actually is at read time, so unlinking is one nulled
    /// column with nothing stranded (ADR-0004).
    /// </summary>
    /// <param name="CharacterId">The Character whose Voice Rules and default Voice drive narration.</param>
    /// <param name="DisplayName">The linked character's primary name — never an alias.</param>
    /// <param name="IsLinked">False when the book narrates with the seed Narrator row.</param>
    public readonly record struct NarratorIdentity(Guid CharacterId, string DisplayName, bool IsLinked)
    {
        /// <summary>Today's behaviour: the seed Narrator row narrates.</summary>
        public static NarratorIdentity Unlinked =>
            new(ProjectDbContext.NarratorId, ProjectDbContext.NarratorName, false);

        /// <summary>
        /// The only reader of <c>Project.NarratorCharacterId</c> outside the command handlers
        /// that write it. A link pointing at no Character resolves to <see cref="Unlinked"/>
        /// rather than failing — a dangling pointer must never take down audio for a whole book.
        /// </summary>
        public static async Task<NarratorIdentity> LoadAsync(ProjectDbContext db, CancellationToken ct = default)
        {
            // One round-trip: the name rides the Projects read as a correlated subquery,
            // so callers on the audio hot path pay a single query for the link.
            var link = await db.Projects
                .AsNoTracking()
                .Select(p => new
                {
                    CharacterId = p.NarratorCharacterId,
                    Name = db.Characters
                        .Where(c => c.Id == p.NarratorCharacterId)
                        .Select(c => c.Name)
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(ct);

            return link is { CharacterId: { } id, Name: { } name }
                ? new NarratorIdentity(id, name, true)
                : Unlinked;
        }
    }
}
