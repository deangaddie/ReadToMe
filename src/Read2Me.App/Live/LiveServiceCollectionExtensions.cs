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
        services.AddSignalR().AddJsonProtocol(o => ConfigurePayloadJson(o.PayloadSerializerOptions));
        services.AddSingleton<LiveConnectionRegistry>();
        services.AddSingleton<ProjectStatusSource>();
        services.AddSingleton<LiveRelay>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveRelay>());
        return services;
    }

    /// <summary>
    /// The hub's payload options. Options converters outrank a type's own <c>[JsonConverter]</c>, so an
    /// enum that is camelCase on the REST wire needs its converter on the property to stay so here.
    /// </summary>
    public static void ConfigurePayloadJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    public static IEndpointRouteBuilder MapLiveHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<LiveHub>(LiveHub.Path);
        return endpoints;
    }
}
