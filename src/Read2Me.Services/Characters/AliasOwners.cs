using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Characters
{
    /// <summary>Roster characters as <see cref="AliasCollisions"/> owners.</summary>
    public static class AliasOwners
    {
        public static AliasOwner ToAliasOwner(this Character c) =>
            new(c.Id, c.Name, c.Aliases.Select(a => a.Name).ToList());
    }
}
