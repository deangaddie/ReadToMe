using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Read2Me.App.Configuration;

/// <summary>Where a configuration value comes from.</summary>
public enum ConfigSourceKind
{
    /// <summary>No source sets the key.</summary>
    None,

    /// <summary>A settings file; <see cref="ConfigSource.Origin"/> names it.</summary>
    File,

    /// <summary>An environment variable, e.g. <c>Workspace__FolderPath</c>.</summary>
    EnvironmentVariable,

    /// <summary>A command-line argument, e.g. <c>--Workspace:FolderPath=…</c>.</summary>
    CommandLine,

    /// <summary>Any other provider (in-memory, user secrets…); <see cref="ConfigSource.Origin"/> names its type.</summary>
    Other,
}

/// <summary>The source whose value for a key takes effect, and that value.</summary>
public sealed record ConfigSource(ConfigSourceKind Kind, string? Origin = null, string? Value = null)
{
    public static readonly ConfigSource None = new(ConfigSourceKind.None);
}

public static class ConfigSources
{
    /// <summary>
    /// The winning source for <paramref name="key"/>: the last provider that sets it, looking
    /// through a chained configuration (the host's, inside the app's) to the provider within.
    /// </summary>
    public static ConfigSource Winning(IConfiguration configuration, string key) =>
        configuration is IConfigurationRoot root ? Winning(root, key) : ConfigSource.None;

    private static ConfigSource Winning(IConfigurationRoot root, string key)
    {
        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value))
                continue;
            return provider switch
            {
                ChainedConfigurationProvider { Configuration: IConfigurationRoot inner } => Winning(inner, key),
                FileConfigurationProvider file => new ConfigSource(ConfigSourceKind.File, file.Source.Path, value),
                EnvironmentVariablesConfigurationProvider => new ConfigSource(ConfigSourceKind.EnvironmentVariable, Value: value),
                CommandLineConfigurationProvider => new ConfigSource(ConfigSourceKind.CommandLine, Value: value),
                _ => new ConfigSource(ConfigSourceKind.Other, provider.GetType().Name, value),
            };
        }
        return ConfigSource.None;
    }
}
