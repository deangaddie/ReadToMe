namespace Read2Me.Data.Entities
{
    /// <summary>
    /// Marks a hierarchy entity (Volume / Part / Chapter / Paragraph / ParagraphItem)
    /// that can appear in a <c>HierarchyMutation</c>. Closed to these five types so the
    /// mutation applier can reject anything it doesn't handle instead of silently skipping it.
    /// </summary>
    public interface IBookEntity
    {
    }
}
