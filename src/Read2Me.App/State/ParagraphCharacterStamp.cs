using System;
using System.Collections.Generic;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.App.State;

public static class ParagraphCharacterStamp
{
    /// Stamp (characterId, character) onto every non-narrator speech item in the list — the
    /// in-memory mirror of SetParagraphCharacterHandler's sweep, so narration and pauses are
    /// untouched (ADR-0006). Idempotent: items already pointing at characterId are skipped,
    /// which is also what makes stamping the narrator itself idempotent. Returns true if
    /// anything changed.
    public static bool Apply(IEnumerable<ParagraphItem> items, Guid? characterId, Character? character)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (ParagraphItemKinds.IsPause(item.ItemType)) continue;
            if (NarrationRule.IsNarration(item)) continue;
            if (item.CharacterId == characterId) continue;
            item.CharacterId = characterId;
            // A hand-flip discards the item's audio; mirror that here so the row shows it back in
            // the audio queue without waiting for a reload (ADR-0006).
            item.AudioFileName = null;
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
