using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.App.Configuration;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class AiWatchdogOptionsTests
{
    private static AiWatchdogOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddAiWatchdogServices(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AiWatchdogOptions>>().Value;
    }

    [Fact]
    public void CarriesDefaults_WhenSectionAbsent()
    {
        var opts = Resolve(new ConfigurationBuilder().Build());

        Assert.Equal(2, opts.ConsecutiveFailuresToTrip);
        Assert.Equal(120, opts.StreamInactivitySeconds);
        Assert.Equal(180, opts.HealthPollTimeoutSeconds);
        Assert.Equal(5, opts.HealthPollIntervalSeconds);
        Assert.Equal(300, opts.WarmupTimeoutSeconds);
        Assert.Equal(2, opts.MaxRecoveryAttempts);
        Assert.True(opts.Enabled);
    }

    [Fact]
    public void BindsFromAiWatchdogSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiWatchdog:ConsecutiveFailuresToTrip"] = "5",
                ["AiWatchdog:StreamInactivitySeconds"] = "30",
                ["AiWatchdog:Enabled"] = "false",
            })
            .Build();

        var opts = Resolve(configuration);

        Assert.Equal(5, opts.ConsecutiveFailuresToTrip);
        Assert.Equal(30, opts.StreamInactivitySeconds);
        Assert.False(opts.Enabled);
        // Untouched keys keep their defaults.
        Assert.Equal(180, opts.HealthPollTimeoutSeconds);
    }

    [Fact]
    public void RegistersRegistryAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddAiWatchdogServices(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<DockerAiServiceRegistry>();
        var b = provider.GetRequiredService<DockerAiServiceRegistry>();
        Assert.Same(a, b);
    }
}
