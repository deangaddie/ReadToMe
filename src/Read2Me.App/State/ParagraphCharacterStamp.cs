using System;
using System.Collections.Generic;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.App.State;

public static class ParagraphCharacterStamp
{
    /// Stamp (characterId, character) onto every Character item in the list.
    /// Narration and Pause items are untouched. Idempotent: items already pointing
    /// at characterId are skipped. Returns true if anything changed.
    public static bool Apply(IEnumerable<ParagraphItem> items, Guid? characterId, Character? character)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (item.ItemType != ParagraphItemType.Character) continue;
            if (item.CharacterId == characterId) continue;
            item.CharacterId = characterId;
            item.Character = character;
            changed = true;
        }
        return changed;
    }

    /// Build minimal Character for display when full entity is not loaded.
    /// Missing Voices/Aliases — only use for chip labels, not audio logic.
    public static Character PlaceholderFor(Guid characterId, string name) =>
        new() { Id = characterId, Name = name };
}
