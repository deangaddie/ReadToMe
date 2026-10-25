using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;

namespace Read2Me.App.Configuration;

/// <summary>
/// The git-ignored per-machine settings file beside the App's other settings files (copy
/// <c>appsettings.Local.example.json</c> to start one). It sits right after the environment's
/// settings file, so environment variables and the command line still override it.
/// </summary>
public static class LocalSettings
{
    public const string FileName = "appsettings.Local.json";

    public static void AddTo(IConfigurationBuilder config, IHostEnvironment environment)
    {
        // Host.CreateDefaultBuilder always adds the environment's file (optional), ahead of
        // environment variables and the command line.
        var environmentFile = $"appsettings.{environment.EnvironmentName}.json";
        var after = config.Sources
            .Select((source, index) => (source, index))
            .LastOrDefault(s => s.source is JsonConfigurationSource json
                && string.Equals(json.Path, environmentFile, StringComparison.OrdinalIgnoreCase));
        if (after.source is null)
            throw new InvalidOperationException($"No {environmentFile} source to add {FileName} after.");

        var local = new JsonConfigurationSource
        {
            FileProvider = config.GetFileProvider(),
            Path = FileName,
            Optional = true,
            ReloadOnChange = true,
        };
        config.Sources.Insert(after.index + 1, local);
    }
}
