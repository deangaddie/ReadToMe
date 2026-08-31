using System.Linq;
using MudBlazor;
using Read2Me.Data;
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

        /// <summary>
        /// How one speech item presents: the narration look for a narrator-stamped item, the
        /// character's own chip for a character, the unattributed look for one with no speaker.
        /// Derived from the speaker, never the item type (ADR-0006), so the row always shows what
        /// will actually be spoken.
        /// </summary>
        public static (string icon, Color color, string label) GetSpeechDisplay(ParagraphItem item)
        {
            if (NarrationRule.IsNarration(item))
                return (Icons.Material.Filled.RecordVoiceOver, Color.Info, "Narration");

            return item.Character is { } character
                ? ("", Color.Primary, character.Name)
                : ("", Color.Warning, "Unknown");
        }

        public static bool IsPauseParagraph(Paragraph p)
        {
            if (p.Items.Count == 0) return true;
            if (p.Items.Count != 1) return false;
            return ParagraphItemKinds.IsPause(p.Items.First().ItemType);
        }
    }
}
