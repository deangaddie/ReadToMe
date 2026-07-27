using System.Collections.Generic;

namespace Read2Me.App.State
{
    /// <summary>
    /// Mutable view model for one proposed character in the discovery review dialog.
    /// The dialog edits these in place and performs no writes; the presenter applies
    /// only the rows the user leaves included.
    /// </summary>
    public sealed class DiscoveredCharacterRow
    {
        public required string Name { get; set; }
        public List<string> Aliases { get; set; } = [];
        public bool Included { get; set; } = true;
        public bool AlreadyExists { get; set; }

        /// <summary>
        /// The roster character this row will resolve onto when applied, if any. Set with
        /// <see cref="AlreadyExists"/>; lets collision detection tell "this row *is* Elizabeth"
        /// from "this row collides with Elizabeth".
        /// </summary>
        public Guid? ExistingCharacterId { get; set; }
    }
}
