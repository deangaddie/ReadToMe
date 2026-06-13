using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// Pure unit tests for ProjectService.SanitizeName — no I/O.
    /// </summary>
    public class ProjectServiceSanitizeTests
    {
        private static ProjectService CreateService() =>
            new(new FakeFileSystem(), new ProjectDbContextProvider(), NullLogger<ProjectService>.Instance);

        [Theory]
        [InlineData("My Book", "my-book")]
        [InlineData("my-book", "my-book")]
        [InlineData("My  Book", "my-book")]
        [InlineData("Book: Part 1!", "book-part-1")]
        [InlineData("!hello!", "hello")]
        [InlineData("", "")]
        [InlineData("!@#", "")]
        public void SanitizeName_ReturnsExpected(string input, string expected)
        {
            var svc = CreateService();
            Assert.Equal(expected, svc.SanitizeName(input));
        }

        [Fact]
        public void SanitizeName_UnicodeTitleLowercased_ValidCharsKept()
        {
            var svc = CreateService();
            var result = svc.SanitizeName("Ëpic Tïtle");
            Assert.Equal("ëpic-tïtle", result);
        }
    }
}
