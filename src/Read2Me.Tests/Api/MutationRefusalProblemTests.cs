using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Read2Me.App.Api;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// The one classification of a refused Book mutation (#357): every reason has a Refusal kind,
    /// and every kind one HTTP answer, so the same refusal answers the same status from every
    /// endpoint.
    /// </summary>
    public class MutationRefusalProblemTests
    {
        public static TheoryData<BookMutationRejection, BookMutationRefusalKind> Reasons => new()
        {
            { BookMutationRejection.Cancelled, BookMutationRefusalKind.Cancelled },
            { BookMutationRejection.Conflict, BookMutationRefusalKind.Contention },
            { BookMutationRejection.Activity, BookMutationRefusalKind.Contention },
            { BookMutationRejection.Blocked, BookMutationRefusalKind.Contention },
            { BookMutationRejection.NotFound, BookMutationRefusalKind.Missing },
            { BookMutationRejection.Validation, BookMutationRefusalKind.Invalid },
        };

        [Theory]
        [MemberData(nameof(Reasons))]
        public void Every_reason_has_its_kind(BookMutationRejection reason, BookMutationRefusalKind kind) =>
            Assert.Equal(kind, reason.Kind());

        [Fact]
        public void The_table_covers_every_reason() =>
            Assert.Equal(
                Enum.GetValues<BookMutationRejection>().Order(),
                Reasons.Select(row => row.Data.Item1).Order());

        [Theory]
        [InlineData(BookMutationRefusalKind.Cancelled, null)]
        [InlineData(BookMutationRefusalKind.Contention, StatusCodes.Status409Conflict)]
        [InlineData(BookMutationRefusalKind.Missing, StatusCodes.Status404NotFound)]
        [InlineData(BookMutationRefusalKind.Invalid, StatusCodes.Status422UnprocessableEntity)]
        public void Every_kind_has_its_status(BookMutationRefusalKind kind, int? status) =>
            Assert.Equal(status, MutationRefusalProblem.StatusOf(kind));

        [Fact]
        public void Every_kind_is_in_the_status_table() =>
            Assert.All(Enum.GetValues<BookMutationRefusalKind>(), kind => MutationRefusalProblem.StatusOf(kind));

        [Fact]
        public void A_cancelled_refusal_rethrows_rather_than_answering() =>
            Assert.Throws<OperationCanceledException>(() => MutationRefusalProblem.From(
                new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "cancelled"), default));

        [Fact]
        public void A_blocked_refusal_keeps_its_books()
        {
            var books = new[] { "A Study in Scarlet" };
            var problem = MutationRefusalProblem.From(
                new BookMutationOutcome.Rejected(BookMutationRejection.Blocked, "Still used by a linked book.")
                {
                    Extensions = new Dictionary<string, object?> { ["books"] = books },
                }, default);

            Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
            Assert.Same(books, problem.ProblemDetails.Extensions["books"]);
        }

        [Fact]
        public void A_gate_refusal_carries_the_gates_kind()
        {
            var refusal = BookMutationRefusal.FromActivity(new ActivityRefusal(ActivityRefusalKind.Deleting, "Project is being deleted"));

            Assert.Equal(
                (BookMutationRejection.Activity, BookMutationRefusalKind.Contention, ActivityRefusalKind.Deleting, "Project is being deleted"),
                (refusal.Reason, refusal.Kind, refusal.Activity!.Kind, refusal.Message));
        }

        /// <summary>Only a Restore is waited out: the queues run an item refused that way again afterwards.</summary>
        [Theory]
        [InlineData(ActivityRefusalKind.Restoring, true)]
        [InlineData(ActivityRefusalKind.Deleting, false)]
        [InlineData(ActivityRefusalKind.Busy, false)]
        public void An_uncommitted_artifact_is_a_restoring_failure_only_while_restoring(ActivityRefusalKind kind, bool restoring)
        {
            var refused = BookMutationRefusal.FromActivity(new ActivityRefusal(kind, "refused"));

            var ex = UncommittedArtifact.AsException(refused, "item audio", default);

            Assert.Equal(restoring, ex is ProjectRestoringException);
            Assert.IsAssignableFrom<InvalidOperationException>(ex);
        }

        [Fact]
        public void A_gate_refusal_answers_as_the_gates_filter_does()
        {
            var problem = MutationRefusalProblem.From(
                BookMutationRefusal.FromActivity(new ActivityRefusal(ActivityRefusalKind.Deleting, "Project is being deleted")), default);

            Assert.Equal(
                (StatusCodes.Status409Conflict, "Project is being deleted", (object?)"deleting"),
                (problem.StatusCode, problem.ProblemDetails.Detail, problem.ProblemDetails.Extensions["activity"]));
        }

        // ── Voice transcript (#357: it answered every refusal with 422) ─────────────

        [Fact]
        public void A_transcript_refused_while_restoring_is_409()
        {
            var answer = VoiceEndpoints.TranscriptAnswer(
                BookMutationRefusal.FromActivity(new ActivityRefusal(ActivityRefusalKind.Restoring, "Project is restoring")),
                "transcript", default);

            var problem = Assert.IsType<ProblemHttpResult>(answer.Result);
            Assert.Equal((StatusCodes.Status409Conflict, "Project is restoring"), (problem.StatusCode, problem.ProblemDetails.Detail));
        }

        [Fact]
        public void A_cancelled_transcript_commit_rethrows_rather_than_answering_422() =>
            Assert.Throws<OperationCanceledException>(() => VoiceEndpoints.TranscriptAnswer(
                new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "cancelled"), "transcript", default));

        [Fact]
        public void A_transcript_for_a_voice_deleted_meanwhile_is_404()
        {
            var answer = VoiceEndpoints.TranscriptAnswer(
                new BookMutationOutcome.Rejected(BookMutationRejection.NotFound, "No such voice."), "transcript", default);

            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ProblemHttpResult>(answer.Result).StatusCode);
        }
    }
}
