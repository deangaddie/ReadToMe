using System.Linq;
using MudBlazor;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.App.Shared
{
    public static class ParagraphItemDisplay
    {
        public static string GetPauseLabel(ParagraphItemType? type) => type switch
        {
            ParagraphItemType.ParagraphPause => "Paragraph pause",
            ParagraphItemType.ChapterPause   => "Chapter pause",
            ParagraphItemType.PartPause      => "Part pause",
            ParagraphItemType.VolumePause    => "Volume pause",
            ParagraphItemType.Pause          => "Pause",
            null                             => "Paragraph pause",
            _                                => type.ToString()!,
        };

        public static (string icon, Color color, string label) GetItemDisplay(ParagraphItemType type) => type switch
        {
            ParagraphItemType.VolumePause    => (Icons.Material.Filled.Pause, Color.Secondary, "Volume Pause"),
            ParagraphItemType.PartPause      => (Icons.Material.Filled.Pause, Color.Secondary, "Part Pause"),
            ParagraphItemType.ChapterPause   => (Icons.Material.Filled.Pause, Color.Secondary, "Chapter Pause"),
            ParagraphItemType.ParagraphPause => (Icons.Material.Filled.PauseCircle, Color.Default, "Paragraph Pause"),
            ParagraphItemType.Pause          => (Icons.Material.Filled.PauseCircleOutline, Color.Default, "Pause"),
            ParagraphItemType.Narration      => (Icons.Material.Filled.RecordVoiceOver, Color.Info, "Narration"),
            _                                => (Icons.Material.Filled.HelpOutline, Color.Default, type.ToString()),
        };

        public static bool IsPauseParagraph(Paragraph p)
        {
            if (p.Items.Count == 0) return true;
            if (p.Items.Count != 1) return false;
            var first = p.Items.First();
            return first.ItemType != ParagraphItemType.Character
                && first.ItemType != ParagraphItemType.Narration;
        }
    }
}
