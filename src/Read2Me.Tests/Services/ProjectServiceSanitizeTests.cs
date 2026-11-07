using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.IO;
using Read2Me.Data;
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
        private static ProjectService CreateService()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.AddSingleton<IFileSystem>(new FakeFileSystem());
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectService>();
            return services.BuildServiceProvider().GetRequiredService<ProjectService>();
        }

        [Theory]
        [InlineData("My Book", "my-book")]
        [InlineData("my-book", "my-book")]
        [InlineData("My  Book", "my-book")]
        [InlineData("Book: Part 1!", "book-part-1")]
        [InlineData("!hello!", "hello")]
        [InlineData("  spaced  ", "spaced")]
        [InlineData("a---b", "a-b")]
        [InlineData("valid_name", "valid_name")]
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
