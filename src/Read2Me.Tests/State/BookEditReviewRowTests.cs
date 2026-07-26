using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.State
{
    public class BookEditReviewRowTests
    {
        private static readonly Guid TargetId = Guid.NewGuid();

        private static ProposedEdit Proposal(
            ProposalStatus status, string oldValue = "Chapter I", string? newValue = "Chapter 1") =>
            new(BookEditTargetKind.ChapterTitle, TargetId, "Book / Chapter I", oldValue,
                status == ProposalStatus.Failed ? null : newValue, status,
                status == ProposalStatus.Failed ? "model returned nothing" : null);

        [Theory]
        [InlineData(ProposalStatus.Proposed, ReviewRowState.Proposed, true)]
        [InlineData(ProposalStatus.NoChange, ReviewRowState.NoChange, false)]
        [InlineData(ProposalStatus.Failed, ReviewRowState.Failed, false)]
        public void WithNoHandEdit_TheProposalStatusShowsThrough(
            ProposalStatus status, ReviewRowState expected, bool appliable)
        {
            var row = new BookEditReviewRow(Proposal(status));

            Assert.Equal(expected, row.State);
            Assert.Equal(appliable, row.IsAppliable);
            Assert.False(row.IsUserEdited);
        }

        [Fact]
        public void AHandEditOnAFailedRow_BecomesAppliableWithTheTypedText()
        {
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Failed));

            row.UserValue = "Chapter One";

            Assert.Equal(ReviewRowState.Edited, row.State);
            Assert.True(row.IsUserEdited);
            Assert.True(row.IsAppliable);

            var item = row.ToEditItem();
            Assert.Equal(BookEditTargetKind.ChapterTitle, item.Kind);
            Assert.Equal(TargetId, item.Id);
            Assert.Equal("Chapter One", item.NewValue);
        }

        [Fact]
        public void AHandEditOnANoChangeRow_BecomesEdited()
        {
            var row = new BookEditReviewRow(
                Proposal(ProposalStatus.NoChange, newValue: "Chapter I"));

            row.UserValue = "Chapter One";

            Assert.Equal(ReviewRowState.Edited, row.State);
            Assert.True(row.IsAppliable);
        }

        [Fact]
        public void TypingTheAiValueBackIn_StillCountsAsAHandEdit()
        {
            // The mark tracks "the user owns this value now", not whether it differs from the AI's.
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Proposed));

            row.UserValue = "Chapter 1";

            Assert.True(row.IsUserEdited);
            Assert.Equal(ReviewRowState.Edited, row.State);
        }

        [Fact]
        public void OnANoChangeRow_RetypingTheSameText_KeepsTheMarkButStaysNoChange()
        {
            // Where the two rules collide: the mark says the user owns the value, the value says
            // there is nothing to apply. Nothing to apply wins.
            var row = new BookEditReviewRow(
                Proposal(ProposalStatus.NoChange, newValue: "Chapter I"));

            row.UserValue = "Chapter I";

            Assert.True(row.IsUserEdited);
            Assert.Equal(ReviewRowState.NoChange, row.State);
            Assert.False(row.IsAppliable);
        }

        [Fact]
        public void AHandEditBackToTheCurrentText_ReadsAsNoChange()
        {
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Proposed));

            row.UserValue = "Chapter I";

            Assert.Equal(ReviewRowState.NoChange, row.State);
            Assert.False(row.IsAppliable);
        }

        [Fact]
        public void AWhitespaceOnlyEdit_IsInvalid()
        {
            // No deletes in V1: an empty value is a mistake, not an instruction.
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Proposed));

            row.UserValue = "   ";

            Assert.Equal(ReviewRowState.Invalid, row.State);
            Assert.False(row.IsAppliable);
        }

        [Fact]
        public void ToEditItem_OnANonAppliableRow_Throws()
        {
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Failed));

            Assert.Throws<InvalidOperationException>(() => row.ToEditItem());
        }

        [Fact]
        public void Revert_RestoresTheAiValueAndDropsTheMark()
        {
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Proposed));
            row.UserValue = "Chapter One";

            row.Revert();

            Assert.False(row.IsUserEdited);
            Assert.Null(row.UserValue);
            Assert.Equal("Chapter 1", row.EffectiveValue);
            Assert.Equal(ReviewRowState.Proposed, row.State);
        }

        [Fact]
        public void ReplaceProposal_TakesTheNewProposalAndClearsAPendingEdit()
        {
            // Retry wins: the user can edit the fresh proposal again afterwards.
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Failed));
            row.UserValue = "Chapter One";

            row.ReplaceProposal(Proposal(ProposalStatus.Proposed, newValue: "Chapter 1"));

            Assert.False(row.IsUserEdited);
            Assert.Equal("Chapter 1", row.EffectiveValue);
            Assert.Equal(ReviewRowState.Proposed, row.State);
        }

        [Fact]
        public void EffectiveValue_PrefersTheUserValue()
        {
            var row = new BookEditReviewRow(Proposal(ProposalStatus.Proposed));
            Assert.Equal("Chapter 1", row.EffectiveValue);

            row.UserValue = "Chapter One";
            Assert.Equal("Chapter One", row.EffectiveValue);
        }
    }
}
