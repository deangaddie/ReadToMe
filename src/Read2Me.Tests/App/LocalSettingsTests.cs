using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App.Configuration;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// The git-ignored <c>appsettings.Local.json</c>: it overrides the environment's settings file,
    /// and environment variables and the command line override it.
    /// </summary>
    public sealed class LocalSettingsTests : IDisposable
    {
        private readonly string _contentRoot =
            Path.Combine(Path.GetTempPath(), "r2me-local-settings", Guid.NewGuid().ToString("N"));

        public LocalSettingsTests()
        {
            Directory.CreateDirectory(_contentRoot);
            Write("appsettings.json", """{ "Ffmpeg": { "Path": "base" }, "Workspace": { "FolderPath": "" } }""");
            Write("appsettings.Development.json", """{ "Ffmpeg": { "Path": "development" } }""");
        }

        public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

        [Fact]
        public void Local_file_overrides_the_environment_settings_file()
        {
            Write("appsettings.Local.json", """{ "Ffmpeg": { "Path": "local" }, "Workspace": { "FolderPath": "local-ws" } }""");

            Assert.Equal("local", Read("Ffmpeg:Path"));
            Assert.Equal("local-ws", Read("Workspace:FolderPath"));
        }

        [Fact]
        public void Without_a_local_file_the_environment_settings_file_wins()
        {
            Assert.Equal("development", Read("Ffmpeg:Path"));
        }

        [Fact]
        public void Command_line_overrides_the_local_file()
        {
            Write("appsettings.Local.json", """{ "Workspace": { "FolderPath": "local" } }""");

            Assert.Equal("command-line", Read("Workspace:FolderPath", "--Workspace:FolderPath=command-line"));
        }

        [Fact]
        public void Environment_variable_overrides_the_local_file()
        {
            // A key no other test or setting uses: the default host reads every environment variable.
            const string variable = "R2mLocalSettingsTests__Probe";
            Write("appsettings.Local.json", """{ "R2mLocalSettingsTests": { "Probe": "local" } }""");
            Environment.SetEnvironmentVariable(variable, "environment");
            try
            {
                Assert.Equal("environment", Read("R2mLocalSettingsTests:Probe"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        private string? Read(string key, params string[] args)
        {
            using var host = Host.CreateDefaultBuilder(args)
                .UseContentRoot(_contentRoot)
                .UseEnvironment(Environments.Development)
                .ConfigureAppConfiguration((context, config) => LocalSettings.AddTo(config, context.HostingEnvironment))
                .Build();
            return host.Services.GetRequiredService<IConfiguration>()[key];
        }

        private void Write(string fileName, string json) => File.WriteAllText(Path.Combine(_contentRoot, fileName), json);
    }
}
