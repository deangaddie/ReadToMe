using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App.Services.SetupCheck;

namespace Read2Me.App.Configuration;

/// <summary>
/// A hosted service that touches app.db or the project folders: it is created and started only
/// when this start has a usable Workspace, so in no-Workspace mode nothing opens a database.
/// </summary>
public sealed class WorkspaceHostedService(WorkspaceStart workspace, Func<IHostedService> create) : IHostedService
{
    /// <summary>The service once started; null in no-Workspace mode.</summary>
    public IHostedService? Running { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!workspace.Usable)
            return Task.CompletedTask;
        Running = create();
        return Running.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Running?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}

public static class WorkspaceHostedServiceExtensions
{
    /// <summary>Adds a hosted service that runs only with a usable Workspace (see <see cref="WorkspaceHostedService"/>).</summary>
    public static IServiceCollection AddWorkspaceHostedService<T>(this IServiceCollection services)
        where T : class, IHostedService =>
        services.AddWorkspaceHostedService(sp => ActivatorUtilities.CreateInstance<T>(sp));

    public static IServiceCollection AddWorkspaceHostedService(
        this IServiceCollection services, Func<IServiceProvider, IHostedService> create) =>
        // Not AddHostedService: it de-duplicates by implementation type, which every gated service shares.
        services.AddSingleton<IHostedService>(sp => new WorkspaceHostedService(sp.GetRequiredService<WorkspaceStart>(), () => create(sp)));
}
