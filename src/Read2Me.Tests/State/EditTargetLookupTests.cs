using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Xunit;

namespace Read2Me.Tests.State
{
    public class EditTargetLookupTests
    {
        private static EditTarget Target(BookEditTargetKind kind, Guid id) =>
            new(kind, id, "Chapter I", "Book / Chapter I", 1, null, null);

        private static BookEditReviewRow Row(BookEditTargetKind kind, Guid id) =>
            new(new ProposedEdit(kind, id, "Book / Chapter I", "Chapter I", "Chapter 1",
                ProposalStatus.Proposed, null));

        [Fact]
        public void FindTarget_ReturnsTheTargetTheRowWasProposedFrom()
        {
            var id = Guid.NewGuid();
            var wanted = Target(BookEditTargetKind.ChapterTitle, id);
            EditTarget[] targets = [Target(BookEditTargetKind.ChapterTitle, Guid.NewGuid()), wanted];

            Assert.Same(wanted, targets.FindTarget(Row(BookEditTargetKind.ChapterTitle, id)));
        }

        [Fact]
        public void FindTarget_MatchesOnKindAsWellAsId()
        {
            // Ids are per-entity, so a paragraph and a title can only be told apart by kind.
            var id = Guid.NewGuid();
            var paragraph = Target(BookEditTargetKind.ParagraphItemText, id);
            EditTarget[] targets = [Target(BookEditTargetKind.ChapterTitle, id), paragraph];

            Assert.Same(paragraph, targets.FindTarget(Row(BookEditTargetKind.ParagraphItemText, id)));
        }

        [Fact]
        public void FindTarget_ReturnsNullWhenNoTargetMatches()
        {
            EditTarget[] targets = [Target(BookEditTargetKind.ChapterTitle, Guid.NewGuid())];

            Assert.Null(targets.FindTarget(Row(BookEditTargetKind.ChapterTitle, Guid.NewGuid())));
        }
    }
}
