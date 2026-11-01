using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Read2Me.App.Api;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Api
{
    /// <summary>
    /// What <c>PUT /api/projects/{folder}/checkpoints/{step}</c> answers: the step segment it
    /// accepts, and the status each mutation outcome becomes. The refusals themselves are proved
    /// where they are decided, in the mutation tests.
    /// </summary>
    public class CheckpointEndpointsTests
    {
        [Theory]
        [InlineData("Structure", PipelineStep.Structure)]
        [InlineData("Speakers", PipelineStep.Speakers)]
        [InlineData("Export", PipelineStep.Export)]
        public void TheStepSegment_isTheStepsMemberName(string segment, PipelineStep expected)
        {
            Assert.True(ApiJson.TryParseName<PipelineStep>(segment, out var step));
            Assert.Equal(expected, step);
        }

        // Enum.TryParse alone would take "3" as Text, "Structure, Text" as a flag mix and "structure" ignoring case.
        [Theory]
        [InlineData("cast")]
        [InlineData("3")]
        [InlineData("Structure, Text")]
        [InlineData("structure")]
        [InlineData("")]
        public void AnUnknownStepSegment_isRejected(string segment)
        {
            Assert.False(ApiJson.TryParseName<PipelineStep>(segment, out _));
        }

        [Fact]
        public void ACommittedOrUnchangedCheckpoint_answers204()
        {
            var receipt = new BookMutationReceipt(new ProjectFolderId("book"), "SetCheckpointMutation",
                Guid.NewGuid(), 1, new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = BookFacets.Checkpoints });

            Assert.IsType<NoContent>(CheckpointEndpoints.ToResult(new BookMutationOutcome.Committed(receipt), default));
            Assert.IsType<NoContent>(CheckpointEndpoints.ToResult(new BookMutationOutcome.NoChange(), default));
        }

        [Theory]
        [InlineData(BookMutationRejection.Validation, StatusCodes.Status422UnprocessableEntity)]
        [InlineData(BookMutationRejection.NotFound, StatusCodes.Status404NotFound)]
        [InlineData(BookMutationRejection.Conflict, StatusCodes.Status409Conflict)]
        [InlineData(BookMutationRejection.Stale, StatusCodes.Status409Conflict)]
        public void ARefusal_answersItsStatusWithTheSentence(BookMutationRejection reason, int status)
        {
            var result = CheckpointEndpoints.ToResult(new BookMutationOutcome.Rejected(reason, "Read the book first."), default);

            var problem = Assert.IsType<ProblemHttpResult>(result);
            Assert.Equal((status, "Read the book first."), (problem.StatusCode, problem.ProblemDetails.Detail));
        }

        [Fact]
        public void ACancelledCommit_rethrowsRatherThanInventingAStatus()
        {
            Assert.Throws<OperationCanceledException>(() => CheckpointEndpoints.ToResult(
                new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "cancelled"), default));
        }

        [Fact]
        public void StatusCheckpoints_areKeyedByStepId_withOnlyTheStepsThatHaveARow()
        {
            var at = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

            var map = CheckpointEndpoints.StatusMap(
            [
                new StepCheckpoint { Step = PipelineStep.Structure, State = CheckpointState.Confirmed, At = at },
                new StepCheckpoint { Step = PipelineStep.Pauses, State = CheckpointState.Skipped, Stale = true, At = at },
            ]);

            Assert.Equal(
                new Dictionary<PipelineStep, StepCheckpointDto>
                {
                    [PipelineStep.Structure] = new(CheckpointState.Confirmed, false, at),
                    [PipelineStep.Pauses] = new(CheckpointState.Skipped, true, at),
                },
                map);
        }

        [Theory]
        [InlineData(CheckpointRequestState.Confirmed, CheckpointState.Confirmed)]
        [InlineData(CheckpointRequestState.Skipped, CheckpointState.Skipped)]
        [InlineData(CheckpointRequestState.Open, null)]
        public void TheBodyState_becomesTheStateToHold_withOpenAsNone(CheckpointRequestState sent, CheckpointState? held)
        {
            Assert.True(CheckpointEndpoints.TryReadState(new SetCheckpointRequest(sent), out var state));
            Assert.Equal(held, state);
        }

        // Either answers 400: an absent state must not read as "open" and delete the row.
        [Fact]
        public void AMissingBodyOrState_isRejected()
        {
            Assert.False(CheckpointEndpoints.TryReadState(null, out _));
            Assert.False(CheckpointEndpoints.TryReadState(new SetCheckpointRequest(null), out _));
        }
    }
}
