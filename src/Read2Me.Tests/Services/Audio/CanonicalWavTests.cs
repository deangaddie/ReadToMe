using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class CanonicalWavTests
    {
        [Theory]
        [InlineData(44, 0)]           // header only
        [InlineData(44 + 48000, 1000)] // one second of 24 kHz mono 16-bit PCM
        [InlineData(44 + 24000, 500)]
        public void DurationMs_IsByteArithmeticOverThePcmPayload(int byteLength, double expectedMs)
        {
            Assert.Equal(expectedMs, CanonicalWav.DurationMs(byteLength));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(20)]
        public void DurationMs_SubHeaderLength_IsZero_NotNegative(int byteLength)
        {
            Assert.Equal(0, CanonicalWav.DurationMs(byteLength));
        }

        [Fact]
        public void RemovedMs_IsTheDifferenceInPayloadDuration()
        {
            Assert.Equal(500, CanonicalWav.RemovedMs(44 + 48000, 44 + 24000));
        }

        [Fact]
        public void RemovedMs_GrownOutput_ReadsZero_NotNegative()
        {
            Assert.Equal(0, CanonicalWav.RemovedMs(44 + 24000, 44 + 48000));
        }
    }
}
