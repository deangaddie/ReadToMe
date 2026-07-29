using System;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Queueing
{
    /// <summary>
    /// The shared retry/settle policy as a table. Pure — no fakes, no database, no processor.
    /// Every case is individually observable here, including the two that are indistinguishable
    /// through a real queue module (<see cref="Disposition.RetryOnce"/> vs.
    /// <see cref="Disposition.RetryAfter"/>, which leave identical queue state).
    /// </summary>
    public class QueueDispositionTests
    {
        private const string Reason = "stalled";

        private static Plan Decide(WorkOutcome outcome, bool hasApplicableWork = true, AttemptState attempts = default)
            => QueueDisposition.Decide(outcome, hasApplicableWork, attempts);

        private static Disposition Now(WorkOutcome outcome, bool hasApplicableWork = true, AttemptState attempts = default)
            => Assert.IsType<Plan.Now>(Decide(outcome, hasApplicableWork, attempts)).D;

        // ── Ok ────────────────────────────────────────────────────────────────

        [Fact]
        public void Ok_WithWorkToApply_AppliesFirst() =>
            Assert.IsType<Plan.ApplyFirst>(Decide(new WorkOutcome.Ok(null)));

        [Fact]
        public void Ok_WithWorkToApply_AppliesFirst_EvenWhenTheAnswerCarriesAReason() =>
            Assert.IsType<Plan.ApplyFirst>(Decide(new WorkOutcome.Ok(Reason)));

        /// <summary>
        /// An answer with nothing to apply — the character queue's empty paragraph — settles
        /// unfinished and must never reach the apply. Elapsed is the executing queue's to supply.
        /// </summary>
        [Fact]
        public void Ok_WithNothingToApply_SettlesUnfinished_WithoutApplying() =>
            Assert.Equal(
                new Disposition.Unfinished(Reason, null),
                Now(new WorkOutcome.Ok(Reason), hasApplicableWork: false));

        // ── Failed ────────────────────────────────────────────────────────────

        [Fact]
        public void Failed_Fails_CarryingTheReason() =>
            Assert.Equal(new Disposition.Failed(Reason), Now(new WorkOutcome.Failed(Reason)));

        [Fact]
        public void Failed_Fails_RegardlessOfBudgetsOrApplicableWork() =>
            Assert.Equal(
                new Disposition.Failed(Reason),
                Now(new WorkOutcome.Failed(Reason), hasApplicableWork: false, new AttemptState(9, 9)));

        // ── Unavailable: the once-only budget ─────────────────────────────────

        [Fact]
        public void Unavailable_FirstTime_RetriesOnce() =>
            Assert.Equal(
                new Disposition.RetryOnce(),
                Now(new WorkOutcome.Unavailable(Reason), attempts: default));

        [Fact]
        public void Unavailable_AfterTheRetryIsSpent_Fails() =>
            Assert.Equal(
                new Disposition.Failed(Reason),
                Now(new WorkOutcome.Unavailable(Reason), attempts: new AttemptState(Retries: 1)));

        /// <summary>Model-load retries are a different budget — they never fund a watchdog retry.</summary>
        [Fact]
        public void Unavailable_IgnoresBusies() =>
            Assert.Equal(
                new Disposition.RetryOnce(),
                Now(new WorkOutcome.Unavailable(Reason), attempts: new AttemptState(Retries: 0, Busies: 5)));

        // ── Busy: the unbounded budget ────────────────────────────────────────

        [Fact]
        public void Busy_FirstTime_RetriesAfterTheBaseBackoff() =>
            Assert.Equal(
                new Disposition.RetryAfter(TimeSpan.FromSeconds(2)),
                Now(new WorkOutcome.Busy(Reason), attempts: default));

        /// <summary>
        /// The row that makes the two budgets structural: an item that has already exhausted the
        /// once-only <see cref="AttemptState.Retries"/> budget must <i>still</i> retry on
        /// <see cref="WorkOutcome.Busy"/>, because failing would evict the model load being awaited.
        /// </summary>
        [Fact]
        public void Busy_RetriesIndefinitely_EvenWithTheOnceOnlyBudgetExhausted() =>
            Assert.Equal(
                new Disposition.RetryAfter(TimeSpan.FromSeconds(30)),
                Now(new WorkOutcome.Busy(Reason), attempts: new AttemptState(Retries: 1, Busies: 5)));

        // ── Backoff curve ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 4)]
        [InlineData(2, 8)]
        [InlineData(3, 16)]
        [InlineData(4, 30)]   // capped
        [InlineData(50, 30)]  // still capped, never overflows
        public void Backoff_IsExponentialWithCap(int attempt, double expectedSeconds) =>
            Assert.Equal(expectedSeconds, QueueDisposition.Backoff(attempt).TotalSeconds);

        [Fact]
        public void Backoff_TreatsANegativeAttemptAsTheFirst() =>
            Assert.Equal(TimeSpan.FromSeconds(2), QueueDisposition.Backoff(-1));
    }
}
