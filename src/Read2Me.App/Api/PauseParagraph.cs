using System.Linq;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Whether a paragraph is a pause paragraph: no items, or a single pause item. The paragraph-level
    /// counterpart of <see cref="ParagraphItemKinds.IsPause"/>, which answers for one item.
    /// </summary>
    public static class PauseParagraph
    {
        public static bool Is(Paragraph p)
        {
            if (p.Items.Count == 0) return true;
            if (p.Items.Count != 1) return false;
            return ParagraphItemKinds.IsPause(p.Items.First().ItemType);
        }
    }
}
