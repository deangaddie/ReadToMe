using System.Linq;
using Read2Me.Data.Entities;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Characters;

namespace Read2Me.App.State
{
    public enum ParaCharacterChip { None, Single, Mixed, Unknown, Partial }

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
            // A Character paragraph is one with at least one non-narrator speech item — the speaker
            // decides, not the item type (ADR-0006).
            var charItems = para.Items
                .Where(i => !ParagraphItemKinds.IsPause(i.ItemType) && !NarrationRule.IsNarration(i))
                .ToList();
            bool isCharPara = charItems.Count > 0;

            // Attribution answers per item, so it can stamp some and leave others unknown. Stamped
            // and unstamped items are counted apart: the mix of the two is its own state (Partial).
            var stamped = charItems.Where(i => i.Character is not null)
                                   .Select(i => i.Character!)
                                   .DistinctBy(c => c.Id)
                                   .ToList();
            bool anyUnknown = charItems.Any(i => i.Character is null);

            ParaCharacterChip chip;
            string? name = null;
            if (!isCharPara) chip = ParaCharacterChip.None;
            else if (stamped.Count == 0) chip = ParaCharacterChip.Unknown;
            else
            {
                if (stamped.Count == 1) name = stamped[0].Name;
                chip = anyUnknown ? ParaCharacterChip.Partial
                     : stamped.Count > 1 ? ParaCharacterChip.Mixed
                     : ParaCharacterChip.Single;
            }

            bool isBusy = queueStatus is not null;
            bool hasUnknown = splitView && anyUnknown;

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
