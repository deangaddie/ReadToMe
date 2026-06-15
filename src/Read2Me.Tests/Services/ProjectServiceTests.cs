using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceTests
    {
        private static ProjectService CreateService(FakeFileSystem fs)
        {
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            return new ProjectService(fs, session, NullLogger<ProjectService>.Instance);
        }

        private static ProjectReader CreateReader(FakeFileSystem fs)
        {
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            return new ProjectReader(session, NullLogger<ProjectReader>.Instance);
        }

        // GetProjects

        [Fact]
        public void GetProjects_WorkspaceEmpty_ReturnsEmpty()
        {
            var fs = new FakeFileSystem();
            var reader = CreateReader(fs);

            var result = reader.GetProjects();

            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_ReturnsDirectoryNamesAlphabetically()
        {
            var fs = new FakeFileSystem();
            fs.SeedFolder("zebra", "alpha", "mango");
            var reader = CreateReader(fs);

            var result = reader.GetProjects();

            Assert.Equal(["alpha", "mango", "zebra"], result);
        }

        // SanitizeName

        [Theory]
        [InlineData("My Project",   "my-project")]
        [InlineData("Hello World!", "hello-world")]
        [InlineData("  spaced  ",   "spaced")]
        [InlineData("a---b",        "a-b")]
        [InlineData("UPPER",        "upper")]
        [InlineData("!!!",          "")]
        [InlineData("valid_name",   "valid_name")]
        public void SanitizeName_ReturnsExpected(string input, string expected)
        {
            var svc = CreateService(new FakeFileSystem());

            Assert.Equal(expected, svc.SanitizeName(input));
        }

        // CreateProject

        [Fact]
        public void CreateProject_CreatesDirectoryAndReturnsTrue()
        {
            var fs = new FakeFileSystem();
            var svc = CreateService(fs);

            var result = svc.CreateProject("My Project");

            Assert.True(result);
            Assert.True(fs.ProjectFolderExists("my-project"));
        }

        [Fact]
        public void CreateProject_ReturnsFalse_WhenAlreadyExists()
        {
            var fs = new FakeFileSystem();
            fs.SeedFolder("my-project");
            var svc = CreateService(fs);

            var result = svc.CreateProject("my-project");

            Assert.False(result);
        }

        [Fact]
        public void CreateProject_ReturnsFalse_WhenNameSanitizesToEmpty()
        {
            var fs = new FakeFileSystem();
            var svc = CreateService(fs);

            var result = svc.CreateProject("!!!");

            Assert.False(result);
        }

        // DeleteProject

        [Fact]
        public void DeleteProject_RemovesDirectory()
        {
            var fs = new FakeFileSystem();
            fs.SeedFolder("my-project");
            var svc = CreateService(fs);

            svc.DeleteProject("my-project");

            Assert.False(fs.ProjectFolderExists("my-project"));
        }

        [Fact]
        public void DeleteProject_DoesNotThrow_WhenDirectoryDoesNotExist()
        {
            var fs = new FakeFileSystem();
            var svc = CreateService(fs);

            var ex = Record.Exception(() => svc.DeleteProject("nonexistent"));

            Assert.Null(ex);
        }

        [Fact]
        public void DeleteProject_RemovesProjectAndFiles()
        {
            var fs = new FakeFileSystem();
            fs.SeedFolder("my-project");
            var svc = CreateService(fs);

            svc.DeleteProject("my-project");

            Assert.False(fs.ProjectFolderExists("my-project"));
        }
    }
}
