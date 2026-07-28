using System;
using System.Collections.Generic;
using Read2Me.Services.Characters;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The character queue's translation surface: an <see cref="AttributionOutcome"/> reduced to the
    /// provider behaviour the queue decides from. Pure — no fakes, no database.
    /// </summary>
    public class CharacterDispositionTests
    {
        private const string Reason = "because";

        /// <summary>
        /// The whole table, in one place. Keyed by status so the exhaustiveness test can assert it
        /// covers every member of the enum.
        /// </summary>
        private static readonly IReadOnlyDictionary<AttributionStatus, WorkOutcome> Table =
            new Dictionary<AttributionStatus, WorkOutcome>
            {
                [AttributionStatus.Resolved] = new WorkOutcome.Ok(null),
                [AttributionStatus.Unknown] = new WorkOutcome.Ok(Reason),
                [AttributionStatus.NoLlmConfigured] = new WorkOutcome.Failed(Reason),
                [AttributionStatus.Failed] = new WorkOutcome.Failed(Reason),
                [AttributionStatus.ServiceUnavailable] = new WorkOutcome.Unavailable(Reason),
                [AttributionStatus.ModelLoading] = new WorkOutcome.Busy(Reason),
            };

        private static WorkOutcome Translate(AttributionStatus status) =>
            new AttributionOutcome(status, null, Reason).Work;

        [Fact]
        public void Resolved_IsOk_WithoutReason() =>
            Assert.Equal(new WorkOutcome.Ok(null), Translate(AttributionStatus.Resolved));

        [Fact]
        public void Unknown_IsOk_CarryingTheReason() =>
            Assert.Equal(new WorkOutcome.Ok(Reason), Translate(AttributionStatus.Unknown));

        [Fact]
        public void NoLlmConfigured_IsFailed() =>
            Assert.Equal(new WorkOutcome.Failed(Reason), Translate(AttributionStatus.NoLlmConfigured));

        [Fact]
        public void Failed_IsFailed() =>
            Assert.Equal(new WorkOutcome.Failed(Reason), Translate(AttributionStatus.Failed));

        [Fact]
        public void ServiceUnavailable_IsUnavailable() =>
            Assert.Equal(new WorkOutcome.Unavailable(Reason), Translate(AttributionStatus.ServiceUnavailable));

        [Fact]
        public void ModelLoading_IsBusy() =>
            Assert.Equal(new WorkOutcome.Busy(Reason), Translate(AttributionStatus.ModelLoading));

        /// <summary>
        /// The row that covers the failure mode none of the others can: a seventh
        /// <see cref="AttributionStatus"/> added later and never translated. Every enum member must
        /// appear in the table above, and translating it must produce that entry rather than
        /// throwing or falling through to <see cref="WorkOutcome.Failed"/>.
        /// </summary>
        [Fact]
        public void EveryAttributionStatus_IsTranslated()
        {
            foreach (var status in Enum.GetValues<AttributionStatus>())
            {
                Assert.True(Table.TryGetValue(status, out var expected),
                    $"AttributionStatus.{status} has no WorkOutcome translation row — add one to the table "
                    + "in AttributionOutcome.Work and here.");
                Assert.Equal(expected, Translate(status));
            }
        }
    }
}
