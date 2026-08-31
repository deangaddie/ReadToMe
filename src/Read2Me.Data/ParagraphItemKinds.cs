using System.Linq.Expressions;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Data
{
    /// <summary>
    /// The one place that answers "is this a pause, or something spoken?" — the only question
    /// <see cref="ParagraphItemType"/> still decides. Who speaks a speech item is
    /// <see cref="NarrationRule"/>'s question, off the speaker (ADR-0006).
    /// </summary>
    public static class ParagraphItemKinds
    {
        // Highest-level pause first — the order PauseRank reports.
        private static readonly ParagraphItemType[] PauseKinds =
        [
            ParagraphItemType.VolumePause,
            ParagraphItemType.PartPause,
            ParagraphItemType.ChapterPause,
            ParagraphItemType.ParagraphPause,
            ParagraphItemType.Pause,
        ];

        /// <summary>True for the five pause kinds; false for anything spoken.</summary>
        public static bool IsPause(ParagraphItemType kind) => Array.IndexOf(PauseKinds, kind) >= 0;

        /// <summary>
        /// Where a pause kind sits in the level ordering — 0 is the highest-level pause, and a
        /// non-pause kind reports -1. Used to keep the biggest pause in a run of adjacent ones.
        /// </summary>
        public static int PauseRank(ParagraphItemType kind) => Array.IndexOf(PauseKinds, kind);

        /// <summary>
        /// The EF-translatable "this item is spoken" test, for queries that must reach SQL.
        /// Compose it with <c>Where(ParagraphItemKinds.IsSpeechExpression)</c>.
        /// </summary>
        public static readonly Expression<Func<ParagraphItem, bool>> IsSpeechExpression =
            item => item.ItemType == ParagraphItemType.Speech;
    }
}
