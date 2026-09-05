using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Read2Me.App.Api;
using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// The one table that turns a Book mutation outcome into what
    /// <c>POST /api/projects/{folder}/commands</c> answers. The contract predates ADR 0007 and does
    /// not move with it: an agent holding the old client sees the same statuses and the same
    /// <c>newEntityId</c> field it always has.
    /// </summary>
    public class BookCommandApiAdapterTests
    {
        private static readonly ProjectFolderId Folder = new("book");

        private static BookCommandResult Committed(Guid? createdId = null) =>
            LegacyBookCommandBridge.AsCommandResult(
                new BookMutationOutcome.Committed(new BookMutationReceipt(
                    Folder, "SomeMutation", Guid.NewGuid(), 1,
                    new BookMutationEffects
                    {
                        Scope = BookMutationScope.Exact,
                        Facets = BookFacets.Attribution,
                        CreatedId = createdId,
                    })));

        private static Guid? IdOf(IResult result) =>
            Assert.IsType<Ok<CommandResponse>>(result).Value!.NewEntityId;

        [Fact]
        public void ACommittedMutation_AnswersWithTheIdentityItCreated()
        {
            var created = Guid.NewGuid();

            var result = BookCommandApiAdapter.ToResult(Committed(created), CancellationToken.None);

            Assert.Equal(created, IdOf(result));
        }

        [Fact]
        public void ACommittedMutationThatCreatedNothing_AnswersWithNoIdentity()
        {
            Assert.Null(IdOf(BookCommandApiAdapter.ToResult(Committed(), CancellationToken.None)));
        }

        /// <summary>
        /// A command that creates something the wire has never reported — the pause insertion — is
        /// still a success with no id.
        /// </summary>
        [Fact]
        public void ACommittedMutationWhoseCommandReportsNoIdentity_AnswersWithNone()
        {
            var result = BookCommandApiAdapter.ToResult(
                Committed(Guid.NewGuid()).WithoutIdentity(), CancellationToken.None);

            Assert.Null(IdOf(result));
        }

        [Fact]
        public void AMutationThatChangedNothing_IsSuccessWithNoIdentity()
        {
            var result = BookCommandApiAdapter.ToResult(
                new BookCommandResult(new BookMutationOutcome.NoChange(), null), CancellationToken.None);

            Assert.Null(IdOf(result));
        }

        /// <summary>
        /// <c>CreateCharacter</c> is idempotent by name: nothing was written the second time, and the
        /// caller still gets the id of whoever answers to it.
        /// </summary>
        [Fact]
        public void AnIdentityResolvedWithoutWriting_IsStillAnswered()
        {
            var existing = Guid.NewGuid();

            var result = BookCommandApiAdapter.ToResult(
                new BookCommandResult(new BookMutationOutcome.NoChange(), existing), CancellationToken.None);

            Assert.Equal(existing, IdOf(result));
        }

        /// <summary>
        /// A refusal the command has always answered as null reaches the adapter already reported as
        /// no-change, so the endpoint keeps answering 200 rather than starting to 422.
        /// </summary>
        [Theory]
        [InlineData(BookMutationRejection.NotFound)]
        [InlineData(BookMutationRejection.Validation)]
        public void ARefusalTheCommandSoftens_IsSuccessWithNoIdentity(BookMutationRejection reason)
        {
            var softened = LegacyBookCommandBridge.AsCommandResult(
                new BookMutationOutcome.Rejected(reason, "nope"), reason);

            Assert.Null(IdOf(BookCommandApiAdapter.ToResult(softened, CancellationToken.None)));
        }

        [Theory]
        [InlineData(BookMutationRejection.Validation)]
        [InlineData(BookMutationRejection.NotFound)]
        [InlineData(BookMutationRejection.Conflict)]
        [InlineData(BookMutationRejection.Stale)]
        public void AnExpectedRefusalTheCommandDoesNotSoften_Is422WithItsReason(BookMutationRejection reason)
        {
            var result = BookCommandApiAdapter.ToResult(
                LegacyBookCommandBridge.AsCommandResult(
                    new BookMutationOutcome.Rejected(reason, "that character is not in this book")),
                CancellationToken.None);

            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
            Assert.Equal("that character is not in this book", problem.ProblemDetails.Detail);
        }

        [Fact]
        public void CancellationBeforeTheCommit_StaysCancellation()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => BookCommandApiAdapter.ToResult(
                LegacyBookCommandBridge.AsCommandResult(new BookMutationOutcome.Rejected(
                    BookMutationRejection.Cancelled, "cancelled before it committed")),
                cts.Token));
        }

        /// <summary>
        /// The other half of that rule, and the one that matters: once the mutation has committed,
        /// a cancelled request is still answered as the success it was. A committed change must
        /// never be reported to an agent as uncommitted.
        /// </summary>
        [Fact]
        public void CancellationAfterTheCommit_IsStillReportedAsCommitted()
        {
            using var cts = new CancellationTokenSource();
            var created = Guid.NewGuid();
            cts.Cancel();

            Assert.Equal(created, IdOf(BookCommandApiAdapter.ToResult(Committed(created), cts.Token)));
        }
    }
}
