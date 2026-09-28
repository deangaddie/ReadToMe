using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ReferenceLimitTests
    {
        private const long Small = 1_000_000;

        [Theory]
        [InlineData(0)]
        [InlineData(8_000)]
        [InlineData(15_000)] // at the soft limit is still within it
        public void Check_UpToFifteenSeconds_IsWithin(double durationMs)
        {
            Assert.Equal(ReferenceLimitVerdict.Within, ReferenceLimit.Check(durationMs, Small));
        }

        [Theory]
        [InlineData(15_001)]
        [InlineData(24_200)]
        [InlineData(30_000)] // at the hard limit is still allowed
        public void Check_OverFifteenUpToThirtySeconds_IsOverSoft(double durationMs)
        {
            Assert.Equal(ReferenceLimitVerdict.OverSoft, ReferenceLimit.Check(durationMs, Small));
        }

        [Theory]
        [InlineData(30_001)]
        [InlineData(95_000)]
        public void Check_OverThirtySeconds_IsOverHard(double durationMs)
        {
            Assert.Equal(ReferenceLimitVerdict.OverHard, ReferenceLimit.Check(durationMs, Small));
        }

        [Fact]
        public void Check_OverFiveMiB_IsOverHard_WhateverTheDuration()
        {
            Assert.Equal(ReferenceLimitVerdict.OverHard, ReferenceLimit.Check(5_000, 5L * 1024 * 1024 + 1));
            Assert.Equal(ReferenceLimitVerdict.Within, ReferenceLimit.Check(5_000, 5L * 1024 * 1024));
        }

        [Fact]
        public void SoftWarning_NamesTheDurationAndTheLimit_OnlyOverTheSoftLimit()
        {
            Assert.Null(ReferenceLimit.SoftWarning(15_000));
            Assert.Null(ReferenceLimit.SoftWarning(null));

            var warning = ReferenceLimit.SoftWarning(18_240);
            Assert.NotNull(warning);
            Assert.Contains("18.2 s", warning);
            Assert.Contains("15 s", warning);
        }

        [Fact]
        public void EnsureWithinHardLimit_OverIt_ThrowsWithTheUploadAdvice()
        {
            var ex = Assert.Throws<ReferenceTooLongException>(
                () => ReferenceLimit.EnsureWithinHardLimit(42_000, Small));

            Assert.Equal(42_000, ex.DurationMs);
            Assert.Contains("42.0 s", ex.Message);
            Assert.Contains("30 s or shorter", ex.Message);
            Assert.Contains("trim it and upload again", ex.Message);
        }

        [Fact]
        public void EnsureWithinHardLimit_OverTheSoftLimitOnly_DoesNotThrow()
        {
            ReferenceLimit.EnsureWithinHardLimit(29_000, Small);
        }
    }
}
