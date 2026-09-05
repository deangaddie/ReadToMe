using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Turns a spoken name into the Character that answers to it, creating one if nobody does. This
    /// is the roster's read-plus-write seam: the attribution queue names speakers in prose, and the
    /// generic <c>CreateCharacter</c> command is the same question asked by an agent.
    /// </summary>
    public class CharacterResolver(ICharacterReader reader, BookMutations mutations)
    {
        public static bool Matches(Character c, string name) =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the id of an existing Character matching <paramref name="name"/> by canonical name
        /// or alias (case-insensitively), creating a new Character if none matches.
        /// <para>
        /// The read comes first so that the common case — a name already on the roster — costs no
        /// write, no revision and no Book View reconciliation. <see cref="CreateCharacterMutation"/>
        /// asks the same question again inside its transaction and answers <c>NoChange</c> when it
        /// finds a match this read missed, so a race creates no duplicate; this then reads once more
        /// for the id that won, because a mutation that changed nothing has no created identity to
        /// report (ADR 0007).
        /// </para>
        /// </summary>
        public virtual async Task<Guid> ResolveOrCreateAsync(ProjectFolderId folder, string name, CancellationToken ct)
        {
            if (await FindAsync(folder, name) is { } existing)
                return existing;

            if (await mutations.CommitAsync(new CreateCharacterMutation(folder, name), ct)
                is BookMutationOutcome.Committed { Receipt.Effects.CreatedId: { } created })
                return created;

            return await FindAsync(folder, name)
                ?? throw new InvalidOperationException(
                    $"CreateCharacterMutation neither created nor found a character named '{name}'.");
        }

        /// <summary>
        /// Whoever already answers to <paramref name="name"/>, or null if nobody does. Public because a
        /// producer that has just been told its create changed nothing still needs the id of whoever
        /// was already there.
        /// </summary>
        public async Task<Guid?> FindAsync(ProjectFolderId folder, string name) =>
            (await reader.GetCharactersWithAliasesAsync(folder)).FirstOrDefault(c => Matches(c, name))?.Id;
    }
}
