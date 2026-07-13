using Read2Me.App.State;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.State
{
    public class ParagraphRowViewModelTests
    {
        private static ParagraphItem Char(Character? c) =>
            new() { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Character, Character = c, CharacterId = c?.Id };

        private static ParagraphItem Narration() =>
            new() { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Narration };

        private static Paragraph Para(params ParagraphItem[] items)
        {
            var p = new Paragraph { Id = Guid.NewGuid() };
            foreach (var i in items) p.Items.Add(i);
            return p;
        }

        private static readonly Character Alice = new() { Id = Guid.NewGuid(), Name = "Alice" };
        private static readonly Character Bob   = new() { Id = Guid.NewGuid(), Name = "Bob" };

        [Fact]
        public void NarrationOnly_IsNotCharacterParagraph_ChipNone()
        {
            var vm = ParagraphRowViewModel.For(Para(Narration()), false, null, false);
            Assert.False(vm.IsCharacterParagraph);
            Assert.Equal(ParaCharacterChip.None, vm.Chip);
        }

        [Fact]
        public void SingleCharacter_ChipSingle_WithName()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(Alice), Char(Alice)), false, null, false);
            Assert.Equal(ParaCharacterChip.Single, vm.Chip);
            Assert.Equal("Alice", vm.SingleCharacterName);
        }

        [Fact]
        public void TwoDistinctCharacters_ChipMixed()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(Alice), Char(Bob)), false, null, false);
            Assert.Equal(ParaCharacterChip.Mixed, vm.Chip);
        }

        [Fact]
        public void CharacterItemWithoutCharacter_ChipUnknown()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(null)), false, null, false);
            Assert.Equal(ParaCharacterChip.Unknown, vm.Chip);
        }

        [Fact]
        public void Queued_IsBusy_HidesOutcome()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(Alice)), false, ParagraphQueueStatus.Queued, hasOutcome: true);
            Assert.True(vm.IsBusy);
            Assert.False(vm.ShowOutcome);
        }

        [Fact]
        public void NotBusy_WithOutcome_ShowsOutcome()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(Alice)), false, null, hasOutcome: true);
            Assert.False(vm.IsBusy);
            Assert.True(vm.ShowOutcome);
        }

        [Fact]
        public void SplitView_WithUnassignedCharItem_HasUnknownInSplit()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(Alice), Char(null)), splitView: true, null, false);
            Assert.True(vm.HasUnknownInSplit);
        }

        [Fact]
        public void NonSplitView_NeverHasUnknownInSplit()
        {
            var vm = ParagraphRowViewModel.For(Para(Char(null)), splitView: false, null, false);
            Assert.False(vm.HasUnknownInSplit);
        }

        [Fact]
        public void AllItemsUnassigned_ChipUnknown()
        {
            // Attribution now stamps items as it applies, so an unstamped item is genuinely unknown —
            // there is no pre-stamp queue overlay to fall back on.
            var vm = ParagraphRowViewModel.For(Para(Char(null)), false, null, false);
            Assert.Equal(ParaCharacterChip.Unknown, vm.Chip);
            Assert.Null(vm.SingleCharacterName);
        }
    }
}
