using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    /// <summary>
    /// The accumulator is pure and fed arrival timestamps, so every test here drives it with
    /// plain arrays. There is no clock and no fake clock.
    /// </summary>
    public class TimingsAccumulatorTests
    {
        /// <summary>A cumulative reading as llama.cpp restates it on each token.</summary>
        private static LlmTimings Reading(int predictedN, double predictedMs) =>
            new(CacheN: null, PromptN: null, PromptMs: null, predictedN, predictedMs);

        private static TimingsAccumulator Fed(params (double AtMs, LlmTimings? Timings)[] chunks)
        {
            var acc = new TimingsAccumulator();
            foreach (var (atMs, timings) in chunks)
                acc.Add(timings, TimeSpan.FromMilliseconds(atMs));
            return acc;
        }

        // ---- Cumulative, not additive ----

        [Fact]
        public void Add_ReplacesTheLatestReading_RatherThanSummingRunningTotals()
        {
            // llama.cpp restates the totals every chunk. Summing these would report 60 tokens.
            var acc = Fed(
                (100, Reading(10, 100)),
                (200, Reading(20, 200)),
                (300, Reading(30, 300)));

            Assert.Equal(30, acc.TokensOut);
            Assert.Equal(300, acc.GenerationMs);
        }

        [Fact]
        public void TokensOutAndGenerationMs_ComeFromTheLastReading_NotTheLargest()
        {
            // A final chunk can restate a lower count than a mid-stream one; the last wins.
            var acc = Fed(
                (100, Reading(30, 300)),
                (200, Reading(12, 400)));

            Assert.Equal(12, acc.TokensOut);
            Assert.Equal(400, acc.GenerationMs);
        }

        [Fact]
        public void Add_NullTimings_IsIgnoredAndDoesNotClearTheLatestReading()
        {
            var acc = Fed(
                (100, Reading(10, 100)),
                (200, null));

            Assert.Equal(10, acc.TokensOut);
            Assert.Equal(100, acc.GenerationMs);
        }

        // ---- Rate ----

        [Fact]
        public void Rate_IsPredictedNOverPredictedMs_NotTheServersReadyMadeFigure()
        {
            var acc = Fed((1000, Reading(40, 500)));

            Assert.Equal(80.0, acc.Rate!.Value, 6);
        }

        [Fact]
        public void Rate_NoReadingsAtAll_IsNull()
        {
            Assert.Null(new TimingsAccumulator().Rate);
            Assert.Null(new TimingsAccumulator().TokensOut);
            Assert.Null(new TimingsAccumulator().GenerationMs);
        }

        [Theory]
        // A fully cache-hit prompt serializes llama.cpp's unguarded divides as null.
        [InlineData(null, 100.0)]
        [InlineData(10, null)]
        [InlineData(null, null)]
        public void Rate_EitherInputAbsent_IsNull(int? predictedN, double? predictedMs)
        {
            var acc = Fed((100, new LlmTimings(null, null, null, predictedN, predictedMs)));

            Assert.Null(acc.Rate);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Rate_NonPositiveGenerationMs_IsNullNotZeroAndNeverDivides(double predictedMs)
        {
            var acc = Fed((100, Reading(10, predictedMs)));

            Assert.Null(acc.Rate);
        }

        // ---- WindowRate ----

        [Fact]
        public void WindowRate_DifferencesAcrossTheWindow_IgnoringWorkThatFellOutOfIt()
        {
            // A slow first second (10 tok/s), then a fast burst. The 3s window sees only the burst.
            var acc = Fed(
                (0, Reading(0, 0)),
                (1000, Reading(10, 1000)),
                (2000, Reading(110, 2000)),
                (5000, Reading(410, 5000)));

            // Baseline is the 1000ms reading (the newest at or before 5000-3000): 400 tokens in 4000ms.
            Assert.Equal(100.0, acc.WindowRate(TimeSpan.FromSeconds(3))!.Value, 6);

            // The whole request averages far lower — proving the window really excluded the slow start.
            Assert.Equal(82.0, acc.Rate!.Value, 6);
        }

        [Fact]
        public void WindowRate_BeforeTheWindowFills_ComputesOverThePartialWindow()
        {
            // 800ms into a request: a 3s window has not filled, and a short request must still
            // show a rate rather than nothing.
            var acc = Fed(
                (400, Reading(20, 400)),
                (800, Reading(40, 800)));

            Assert.Equal(50.0, acc.WindowRate(TimeSpan.FromSeconds(3))!.Value, 6);
        }

        [Fact]
        public void WindowRate_SingleReading_MatchesTheRequestRate()
        {
            var acc = Fed((500, Reading(25, 500)));

            Assert.Equal(50.0, acc.WindowRate(TimeSpan.FromSeconds(3))!.Value, 6);
            Assert.Equal(acc.Rate!.Value, acc.WindowRate(TimeSpan.FromSeconds(3))!.Value, 6);
        }

        [Fact]
        public void WindowRate_NothingDifferenceableArrived_IsNull()
        {
            var acc = Fed(
                (100, null),
                (200, new LlmTimings(null, null, null, PredictedN: 10, PredictedMs: null)));

            Assert.Null(acc.WindowRate(TimeSpan.FromSeconds(3)));
        }

        [Fact]
        public void WindowRate_GenerationStalledAcrossTheWindow_IsNullNotZeroDivision()
        {
            // Generation stopped advancing at 1000ms but chunks kept arriving: the baseline sits
            // outside the window and restates identical totals, so the window's delta is 0ms.
            var acc = Fed(
                (1000, Reading(40, 500)),
                (5000, Reading(40, 500)));

            Assert.Null(acc.WindowRate(TimeSpan.FromSeconds(3)));
        }

        [Fact]
        public void WindowRate_ReadingsWithAbsentFields_AreSkippedRatherThanBreakingTheDifference()
        {
            var acc = Fed(
                (0, Reading(0, 0)),
                (1000, new LlmTimings(null, null, null, PredictedN: null, PredictedMs: null)),
                (2000, Reading(100, 2000)));

            Assert.Equal(50.0, acc.WindowRate(TimeSpan.FromSeconds(3))!.Value, 6);
        }
    }
}
