using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.App.Configuration;
using Read2Me.Core.Configuration;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>The committed settings ship the Workspace folder empty; the host refuses to start without one.</summary>
    public class WorkspaceOptionsValidationTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void An_unset_workspace_folder_fails_naming_the_local_settings_file(string folderPath)
        {
            var options = Resolve(folderPath);

            var error = Assert.Throws<OptionsValidationException>(() => options.Value);
            Assert.Contains("Workspace:FolderPath", error.Message);
            Assert.Contains("appsettings.Local.json", error.Message);
        }

        [Fact]
        public void A_set_workspace_folder_resolves()
        {
            Assert.Equal("D:/ws", Resolve("D:/ws").Value.FolderPath);
        }

        private static IOptions<WorkspaceOptions> Resolve(string folderPath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:FolderPath"] = folderPath })
                .Build();
            return new ServiceCollection()
                .AddProjectServices(configuration)
                .BuildServiceProvider()
                .GetRequiredService<IOptions<WorkspaceOptions>>();
        }
    }
}
