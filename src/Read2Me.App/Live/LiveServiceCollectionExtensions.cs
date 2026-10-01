using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Read2Me.App.Live;

public static class LiveServiceCollectionExtensions
{
    /// <summary>
    /// SignalR with the same JSON conventions as the agent API (camelCase, enums as names, nulls
    /// omitted) plus the hub's singletons. <c>/hubs/live</c> is the only hub, so these are the
    /// options every hub message is written with.
    /// </summary>
    public static IServiceCollection AddLiveHub(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(o =>
        {
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.PayloadSerializerOptions.DictionaryKeyPolicy = null;
            o.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddSingleton<LiveConnectionRegistry>();
        services.AddSingleton<ProjectStatusSource>();
        services.AddSingleton<LiveRelay>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveRelay>());
        return services;
    }

    public static IEndpointRouteBuilder MapLiveHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<LiveHub>(LiveHub.Path);
        return endpoints;
    }
}
