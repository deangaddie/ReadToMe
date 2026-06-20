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
        /// <summary>
        /// Resolved character from the queue, used as a display overlay in split view
        /// when items have not yet been stamped with a real entity.
        /// </summary>
        public ResolvedCharacter? ResolvedOverlay { get; }

        private ParagraphRowViewModel(
            bool isCharacterParagraph, bool isBusy, bool showOutcome,
            bool hasUnknownInSplit, ParaCharacterChip chip, string? singleCharacterName,
            ResolvedCharacter? resolvedOverlay)
        {
            IsCharacterParagraph = isCharacterParagraph;
            IsBusy = isBusy;
            ShowOutcome = showOutcome;
            HasUnknownInSplit = hasUnknownInSplit;
            Chip = chip;
            SingleCharacterName = singleCharacterName;
            ResolvedOverlay = resolvedOverlay;
        }

        public static ParagraphRowViewModel For(
            Paragraph para, bool splitView,
            ParagraphQueueStatus? queueStatus,
            bool hasOutcome,
            ResolvedCharacter? resolvedOverlay = null)
        {
            var charItems = para.Items.Where(i => i.ItemType == ParagraphItemType.Character).ToList();
            bool isCharPara = charItems.Count > 0;
            var distinct = charItems.Select(i => i.Character).DistinctBy(c => c?.Id).ToList();

            ParaCharacterChip chip;
            string? name = null;
            if (!isCharPara) chip = ParaCharacterChip.None;
            else if (distinct.Count > 1) chip = ParaCharacterChip.Mixed;
            else if (distinct[0] is null)
            {
                // If every character item is unresolved but queue has resolved one, show it as Single
                if (resolvedOverlay is not null)
                { chip = ParaCharacterChip.Single; name = resolvedOverlay.Name; }
                else chip = ParaCharacterChip.Unknown;
            }
            else { chip = ParaCharacterChip.Single; name = distinct[0]!.Name; }

            bool isBusy = queueStatus is not null;
            // Unknown in split: item.Character is null AND no resolved overlay available
            bool hasUnknown = splitView && charItems.Any(i => i.Character is null && resolvedOverlay is null);

            return new ParagraphRowViewModel(
                isCharacterParagraph: isCharPara,
                isBusy: isBusy,
                showOutcome: !isBusy && hasOutcome,
                hasUnknownInSplit: hasUnknown,
                chip: chip,
                singleCharacterName: name,
                resolvedOverlay: resolvedOverlay);
        }
    }
}
