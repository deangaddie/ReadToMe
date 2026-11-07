using System.Text.Json;
using Read2Me.App.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Read2Me.App.Live;

public static class LiveServiceCollectionExtensions
{
    /// <summary>
    /// SignalR with the agent API's JSON conventions (camelCase, enums as names, nulls sent) plus the
    /// hub's singletons. <c>/hubs/live</c> is the only hub, so these are the options every hub message
    /// is written with.
    /// </summary>
    public static IServiceCollection AddLiveHub(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(o => ConfigurePayloadJson(o.PayloadSerializerOptions));
        services.AddSingleton<LiveConnectionRegistry>();
        services.AddSingleton<ProjectStatusSource>();
        services.AddSingleton<LiveRelay>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveRelay>());
        return services;
    }

    /// <summary>
    /// The hub's payload options: the agent API's wire form (<see cref="ApiJson"/>), nulls sent, so a
    /// nullable field generated as <c>T | null</c> (<see cref="LiveContractTransformer"/>) is always there.
    /// </summary>
    public static void ConfigurePayloadJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        ApiJson.Configure(options);
    }

    public static IEndpointRouteBuilder MapLiveHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<LiveHub>(LiveHub.Path);
        return endpoints;
    }
}
