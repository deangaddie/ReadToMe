using System.Linq;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Characters;

namespace Read2Me.App.State
{
    public enum ParaCharacterChip { None, Single, Mixed, Unknown }

    /// <summary>
    /// Pure presentation decisions for one paragraph row — no MudBlazor, no async.
    /// Razor maps Chip → Color/Label/Variant.
    /// </summary>
    public readonly struct ParagraphRowViewModel
    {
        public bool IsCharacterParagraph { get; }
        public bool IsBusy { get; }
        public bool ShowOutcome { get; }
        public bool HasUnknownInSplit { get; }
        public ParaCharacterChip Chip { get; }
        public string? SingleCharacterName { get; }

        private ParagraphRowViewModel(
            bool isCharacterParagraph, bool isBusy, bool showOutcome,
            bool hasUnknownInSplit, ParaCharacterChip chip, string? singleCharacterName)
        {
            IsCharacterParagraph = isCharacterParagraph;
            IsBusy = isBusy;
            ShowOutcome = showOutcome;
            HasUnknownInSplit = hasUnknownInSplit;
            Chip = chip;
            SingleCharacterName = singleCharacterName;
        }

        public static ParagraphRowViewModel For(
            Paragraph para, bool splitView,
            ParagraphQueueStatus? queueStatus,
            bool hasOutcome)
        {
            var charItems = para.Items.Where(i => i.ItemType == ParagraphItemType.Character).ToList();
            bool isCharPara = charItems.Count > 0;
            var distinct = charItems.Select(i => i.Character).DistinctBy(c => c?.Id).ToList();

            ParaCharacterChip chip;
            string? name = null;
            if (!isCharPara) chip = ParaCharacterChip.None;
            else if (distinct.Count > 1) chip = ParaCharacterChip.Mixed;
            else if (distinct[0] is null) chip = ParaCharacterChip.Unknown;
            else { chip = ParaCharacterChip.Single; name = distinct[0]!.Name; }

            bool isBusy = queueStatus is not null;
            bool hasUnknown = splitView && charItems.Any(i => i.Character is null);

            return new ParagraphRowViewModel(
                isCharacterParagraph: isCharPara,
                isBusy: isBusy,
                showOutcome: !isBusy && hasOutcome,
                hasUnknownInSplit: hasUnknown,
                chip: chip,
                singleCharacterName: name);
        }
    }
}
