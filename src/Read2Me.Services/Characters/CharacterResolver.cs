using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Characters
{
    public class CharacterResolver(IProjectReader reader, IBookCommandHandler commands)
    {
        public static bool Matches(Character c, string name) =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the id of an existing Character matching <paramref name="name"/> by canonical name
        /// or alias (case-insensitive), creating a new Character if none matches.
        /// </summary>
        public virtual async Task<Guid> ResolveOrCreateAsync(ProjectFolderId folder, string name, CancellationToken ct)
        {
            var characters = await reader.GetCharactersWithAliasesAsync(folder);
            var existing = characters.FirstOrDefault(c => Matches(c, name));
            if (existing != null)
                return existing.Id;

            var created = await commands.ExecuteAsync(new CreateCharacterCommand(folder, name), ct);
            return created ?? throw new InvalidOperationException(
                $"CreateCharacterCommand returned null for name '{name}'");
        }
    }
}
