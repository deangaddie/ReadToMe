using Read2Me.Data.Enums;
using Read2Me.Services.Checkpoints;
using Xunit;

namespace Read2Me.Tests.Services.Checkpoints
{
    /// <summary>
    /// Which Pipeline steps take which checkpoint states. The web's pills mirror this table, so every
    /// cell is pinned here rather than inferred from a step's kind.
    /// </summary>
    public class CheckpointMatrixTests
    {
        public static TheoryData<PipelineStep, CheckpointState?, bool> Cells => new()
        {
            { PipelineStep.Import, CheckpointState.Confirmed, false },
            { PipelineStep.Import, CheckpointState.Skipped, false },
            { PipelineStep.Import, null, false },
            { PipelineStep.Structure, CheckpointState.Confirmed, true },
            { PipelineStep.Structure, CheckpointState.Skipped, true },
            { PipelineStep.Structure, null, true },
            { PipelineStep.Speakers, CheckpointState.Confirmed, true },
            { PipelineStep.Speakers, CheckpointState.Skipped, false },
            { PipelineStep.Speakers, null, true },
            { PipelineStep.Text, CheckpointState.Confirmed, true },
            { PipelineStep.Text, CheckpointState.Skipped, true },
            { PipelineStep.Text, null, true },
            { PipelineStep.Voices, CheckpointState.Confirmed, false },
            { PipelineStep.Voices, CheckpointState.Skipped, false },
            { PipelineStep.Voices, null, false },
            { PipelineStep.Titles, CheckpointState.Confirmed, true },
            { PipelineStep.Titles, CheckpointState.Skipped, true },
            { PipelineStep.Titles, null, true },
            { PipelineStep.Pauses, CheckpointState.Confirmed, false },
            { PipelineStep.Pauses, CheckpointState.Skipped, true },
            { PipelineStep.Pauses, null, true },
            { PipelineStep.Audio, CheckpointState.Confirmed, false },
            { PipelineStep.Audio, CheckpointState.Skipped, false },
            { PipelineStep.Audio, null, false },
            { PipelineStep.Export, CheckpointState.Confirmed, false },
            { PipelineStep.Export, CheckpointState.Skipped, false },
            { PipelineStep.Export, null, false },
        };

        // null is "open": clearing is allowed exactly where some state could be set.
        [Theory]
        [MemberData(nameof(Cells))]
        public void Refusal_isNullExactlyForTheAllowedCells(PipelineStep step, CheckpointState? state, bool allowed)
        {
            Assert.Equal(allowed, CheckpointMatrix.Refusal(step, state) is null);
        }

        [Fact]
        public void Refusal_namesWhatTheStepDoesTake()
        {
            Assert.Equal("Speakers can only be confirmed.",
                CheckpointMatrix.Refusal(PipelineStep.Speakers, CheckpointState.Skipped));
            Assert.Equal("Pauses can only be skipped.",
                CheckpointMatrix.Refusal(PipelineStep.Pauses, CheckpointState.Confirmed));
            Assert.Equal("Voices is done by its data, not confirmed or skipped.",
                CheckpointMatrix.Refusal(PipelineStep.Voices, null));
        }
    }
}
