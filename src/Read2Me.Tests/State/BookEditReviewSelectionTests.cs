using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.State
{
    public class BookEditReviewSelectionTests
    {
        private static BookEditReviewRow Row(
            ProposalStatus status, string oldValue = "Chapter I", string? newValue = "Chapter 1") =>
            new(Verdict(
                new ProposedEdit(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), "Book / " + oldValue,
                    oldValue, null, status, null),
                status, newValue));

        /// <summary>What a retry hands back for a row that already exists.</summary>
        private static ProposedEdit RetryResult(
            BookEditReviewRow row, ProposalStatus status, string? newValue) =>
            Verdict(row.Proposal, status, newValue);

        private static ProposedEdit Verdict(
            ProposedEdit edit, ProposalStatus status, string? newValue) =>
            edit with
            {
                NewValue = status == ProposalStatus.Failed ? null : newValue,
                Status = status,
                FailureReason = status == ProposalStatus.Failed ? "model returned nothing" : null,
            };

        private static BookEditReviewSelection Selection(params BookEditReviewRow[] rows) =>
            new(rows);

        [Fact]
        public void OnCreation_EveryAppliableRowIsSelected()
        {
            var proposed = Row(ProposalStatus.Proposed);
            var noChange = Row(ProposalStatus.NoChange);
            var failed = Row(ProposalStatus.Failed);

            var selection = Selection(proposed, noChange, failed);

            Assert.True(selection.IsSelected(proposed));
            Assert.False(selection.IsSelected(noChange));
            Assert.False(selection.IsSelected(failed));
            Assert.Equal(1, selection.Count);
            Assert.Equal(1, selection.SelectableCount);
        }

        [Fact]
        public void ANonAppliableRow_CannotBeSelectedByHand()
        {
            var failed = Row(ProposalStatus.Failed);
            var selection = Selection(failed);

            selection.Set(failed, true);

            Assert.False(selection.IsSelected(failed));
        }

        [Fact]
        public void SetFalse_Deselects()
        {
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);

            selection.Set(row, false);

            Assert.False(selection.IsSelected(row));
            Assert.Equal(0, selection.Count);
        }

        [Fact]
        public void AFirstHandEditOnAFailedRow_AutoSelectsIt()
        {
            var failed = Row(ProposalStatus.Failed);
            var selection = Selection(failed);

            selection.SetValue(failed, "Chapter One");

            Assert.True(selection.IsSelected(failed));
            Assert.Equal(1, selection.Count);
            Assert.Equal(1, selection.SelectableCount);
        }

        [Fact]
        public void EditingARowTheUserHadDeselected_DoesNotReSelectIt()
        {
            // Auto-select is a convenience for rows that just became appliable, not a rule that
            // overrides a deliberate uncheck.
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);
            selection.Set(row, false);

            selection.SetValue(row, "Chapter One");

            Assert.False(selection.IsSelected(row));
        }

        [Fact]
        public void ARowEditedIntoInvalid_IsDeselected()
        {
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);

            selection.SetValue(row, "  ");

            Assert.False(selection.IsSelected(row));
            Assert.Equal(0, selection.Count);
            Assert.Equal(0, selection.SelectableCount);
        }

        [Fact]
        public void ARowEditedBackToTheCurrentText_IsDeselected()
        {
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);

            selection.SetValue(row, "Chapter I");

            Assert.False(selection.IsSelected(row));
        }

        [Fact]
        public void RevertingAnEditedFailedRow_DropsItsSelection()
        {
            var failed = Row(ProposalStatus.Failed);
            var selection = Selection(failed);
            selection.SetValue(failed, "Chapter One");

            selection.Revert(failed);

            Assert.False(failed.IsUserEdited);
            Assert.False(selection.IsSelected(failed));
        }

        [Fact]
        public void SelectAll_TakesEveryAppliableRowIncludingHandEditedOnes()
        {
            var proposed = Row(ProposalStatus.Proposed);
            var failed = Row(ProposalStatus.Failed);
            var selection = Selection(proposed, failed);
            selection.Clear();
            selection.SetValue(failed, "Chapter One");
            selection.Set(failed, false);

            selection.SelectAll();

            Assert.True(selection.IsSelected(proposed));
            Assert.True(selection.IsSelected(failed));
            Assert.Equal(2, selection.Count);
        }

        [Fact]
        public void Clear_DeselectsEverything()
        {
            var selection = Selection(Row(ProposalStatus.Proposed), Row(ProposalStatus.Proposed));

            selection.Clear();

            Assert.Equal(0, selection.Count);
        }

        [Fact]
        public void ToEditItems_SendsOnlyTheSelectedAppliableRows_WithTheirEffectiveValues()
        {
            var proposed = Row(ProposalStatus.Proposed);
            var deselected = Row(ProposalStatus.Proposed);
            var edited = Row(ProposalStatus.Failed);
            var untouchedFailure = Row(ProposalStatus.Failed);
            var selection = Selection(proposed, deselected, edited, untouchedFailure);
            selection.Set(deselected, false);
            selection.SetValue(edited, "Chapter One");

            var items = selection.ToEditItems();

            Assert.Equal([proposed.Proposal.Id, edited.Proposal.Id], items.Select(i => i.Id));
            Assert.Equal(["Chapter 1", "Chapter One"], items.Select(i => i.NewValue));
        }

        [Fact]
        public void Count_IgnoresASelectedRowThatStoppedBeingAppliable()
        {
            // Belt and braces: Apply's count and Apply's payload read the same rows, so the button
            // can never promise more edits than it sends.
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);
            row.UserValue = "  ";

            Assert.Equal(0, selection.Count);
            Assert.Empty(selection.ToEditItems());
        }

        [Fact]
        public void ReplaceProposal_WithAProposedRetryResult_SelectsTheRowAndDropsTheHandEdit()
        {
            // The user asked for this row again, so a usable answer is wanted — even if they had
            // unticked it, or typed over the answer the retry just replaced.
            var row = Row(ProposalStatus.Failed);
            var selection = Selection(row);
            selection.SetValue(row, "Chapter One");
            selection.Set(row, false);

            selection.ReplaceProposal(row, RetryResult(row, ProposalStatus.Proposed, "Chapter 1"));

            Assert.False(row.IsUserEdited);
            Assert.Equal("Chapter 1", row.EffectiveValue);
            Assert.True(selection.IsSelected(row));
            Assert.Equal(1, selection.Count);
        }

        [Theory]
        [InlineData(ProposalStatus.Failed)]
        [InlineData(ProposalStatus.NoChange)]
        public void ReplaceProposal_WithAnUnusableRetryResult_DeselectsTheRow(ProposalStatus status)
        {
            var row = Row(ProposalStatus.Proposed);
            var selection = Selection(row);

            selection.ReplaceProposal(row, RetryResult(row, status, "Chapter I"));

            Assert.False(selection.IsSelected(row));
            Assert.Equal(0, selection.Count);
        }

        [Fact]
        public void HandEditedCount_CountsTheRowsAConfirmWouldDiscard()
        {
            var edited = Row(ProposalStatus.Proposed);
            var reverted = Row(ProposalStatus.Proposed);
            var selection = Selection(edited, reverted, Row(ProposalStatus.Failed));
            selection.SetValue(edited, "Chapter One");
            selection.SetValue(reverted, "Chapter Two");
            selection.Revert(reverted);

            Assert.Equal(1, selection.HandEditedCount);
        }
    }
}
