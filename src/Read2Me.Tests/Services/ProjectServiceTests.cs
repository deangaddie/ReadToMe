using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Services;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceTests
    {
        private const string Workspace = "C:\\workspace";

        private static ProjectService CreateService(FakeFileSystem fs, string? workspace = null)
        {
            var opts = Options.Create(new WorkspaceOptions { FolderPath = workspace ?? Workspace });
            return new ProjectService(opts, fs, NullLogger<ProjectService>.Instance);
        }

        // GetProjects

        [Fact]
        public void GetProjects_WorkspaceDoesNotExist_ReturnsEmpty()
        {
            var fs = new FakeFileSystem();
            var svc = CreateService(fs);

            var result = svc.GetProjects();

            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_ReturnsDirectoryNamesAlphabetically()
        {
            var fs = new FakeFileSystem();
            fs.Seed(
                Workspace,
                Path.Combine(Workspace, "zebra"),
                Path.Combine(Workspace, "alpha"),
                Path.Combine(Workspace, "mango")
            );
            var svc = CreateService(fs);

            var result = svc.GetProjects();

            Assert.Equal(["alpha", "mango", "zebra"], result);
        }

        [Fact]
        public void GetProjects_IgnoresNestedDirectories()
        {
            var fs = new FakeFileSystem();
            fs.Seed(
                Workspace,
                Path.Combine(Workspace, "project-a"),
                Path.Combine(Workspace, "project-a", "nested")
            );
            var svc = CreateService(fs);

            var result = svc.GetProjects();

            Assert.Equal(["project-a"], result);
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
            fs.Seed(Workspace);
            var svc = CreateService(fs);

            var result = svc.CreateProject("My Project");

            Assert.True(result);
            Assert.True(fs.DirectoryExists(Path.Combine(Workspace, "my-project")));
        }

        [Fact]
        public void CreateProject_ReturnsFalse_WhenAlreadyExists()
        {
            var fs = new FakeFileSystem();
            fs.Seed(Workspace, Path.Combine(Workspace, "my-project"));
            var svc = CreateService(fs);

            var result = svc.CreateProject("my-project");

            Assert.False(result);
        }

        [Fact]
        public void CreateProject_ReturnsFalse_WhenNameSanitizesToEmpty()
        {
            var fs = new FakeFileSystem();
            fs.Seed(Workspace);
            var svc = CreateService(fs);

            var result = svc.CreateProject("!!!");

            Assert.False(result);
        }

        // DeleteProject

        [Fact]
        public void DeleteProject_RemovesDirectory()
        {
            var fs = new FakeFileSystem();
            var projectPath = Path.Combine(Workspace, "my-project");
            fs.Seed(Workspace, projectPath);
            var svc = CreateService(fs);

            svc.DeleteProject("my-project");

            Assert.False(fs.DirectoryExists(projectPath));
        }

        [Fact]
        public void DeleteProject_DoesNotThrow_WhenDirectoryDoesNotExist()
        {
            var fs = new FakeFileSystem();
            fs.Seed(Workspace);
            var svc = CreateService(fs);

            var ex = Record.Exception(() => svc.DeleteProject("nonexistent"));

            Assert.Null(ex);
        }

        [Fact]
        public void DeleteProject_RemovesNestedContents()
        {
            var fs = new FakeFileSystem();
            var projectPath = Path.Combine(Workspace, "my-project");
            var nestedPath = Path.Combine(projectPath, "chapter-1");
            fs.Seed(Workspace, projectPath, nestedPath);
            var svc = CreateService(fs);

            svc.DeleteProject("my-project");

            Assert.False(fs.DirectoryExists(projectPath));
            Assert.False(fs.DirectoryExists(nestedPath));
        }
    }
}
