using System.Linq.Expressions;
using Read2Me.Data.Entities;

namespace Read2Me.Data
{
    /// <summary>
    /// The one place that answers "is this item narration?". Narration is a property of the
    /// speaker, not of <c>ParagraphItem.ItemType</c>: an item is narration exactly when its
    /// speaker is the narrator sentinel (<see cref="ProjectDbContext.NarratorId"/>).
    /// <para>
    /// Every consumer — readers, resolvers, command handlers, API DTOs, row view models —
    /// asks through here rather than comparing to the sentinel (or the item type) inline,
    /// so the rule cannot drift apart across layers.
    /// </para>
    /// <para>
    /// This compares against the stored sentinel; it does not resolve the narrator link.
    /// Who the narrator actually is stays <see cref="NarratorIdentity"/>'s job (ADR-0004).
    /// </para>
    /// </summary>
    public static class NarrationRule
    {
        /// <summary>
        /// The EF-translatable form, for readers that ask this inside LINQ which must reach
        /// SQL. Compose it with <c>Where(NarrationRule.IsNarrationExpression)</c>.
        /// </summary>
        public static readonly Expression<Func<ParagraphItem, bool>> IsNarrationExpression =
            item => item.CharacterId == ProjectDbContext.NarratorId;

        private static readonly Func<ParagraphItem, bool> Predicate = IsNarrationExpression.Compile();

        /// <summary>The in-memory form, for an item already loaded.</summary>
        public static bool IsNarration(ParagraphItem item) => Predicate(item);

        /// <summary>
        /// The in-memory form for a speaker read on its own — a projection row that carries the
        /// speaker without the whole entity, for instance.
        /// </summary>
        public static bool IsNarration(Guid? characterId) => characterId == ProjectDbContext.NarratorId;
    }
}
