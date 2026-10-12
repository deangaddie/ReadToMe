using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Read2Me.E2eTests.Infrastructure;

public static class LiveHubConnectionExtensions
{
    /// <summary>
    /// Starts the connection and returns once the hub has put it in the global group, so a push the
    /// test triggers next can't miss it. <see cref="HubConnection.StartAsync"/> returns at the
    /// handshake, before the server runs <c>OnConnectedAsync</c> (which joins the group); the hub
    /// answers invocations only after that, so one read-only round trip is the signal.
    /// </summary>
    public static async Task StartJoinedAsync(this HubConnection hub)
    {
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("GetSnapshot");
    }
}
