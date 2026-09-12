using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.App.Live;

/// <summary>
/// <c>/hubs/live</c>. Every connection is in <c>global</c>; it opts into <c>project:{folder}</c>
/// and <c>stream:llm</c> / <c>stream:audio</c> groups. Pushes come from <see cref="LiveRelay"/>;
/// the hub itself only routes membership, answers snapshots and replays a journal's current turn to
/// a late stream joiner.
/// </summary>
public sealed class LiveHub(
    LiveConnectionRegistry registry,
    LiveRelay relay,
    ProjectStatusSource status,
    EventJournal<LlmStreamEvent> llmJournal,
    EventJournal<AudioGenEvent> audioJournal,
    ILogger<LiveHub> logger) : Hub<ILiveClient>
{
    public const string Path = "/hubs/live";

    public override async Task OnConnectedAsync()
    {
        registry.Connected(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Global);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Joins the project group and returns its full current status so the caller starts consistent.</summary>
    public async Task<ProjectSnapshot> JoinProject(string folder)
    {
        var id = ParseFolder(folder);
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Project(id.Value));
        registry.JoinProject(Context.ConnectionId, id.Value);
        logger.LogDebug("Live group {Group} joined by {ConnectionId}; members now {Count}",
            LiveGroups.Project(id.Value), Context.ConnectionId, registry.ProjectMemberCount(id.Value));
        return status.Snapshot(id);
    }

    public async Task LeaveProject(string folder)
    {
        var id = ParseFolder(folder);
        registry.LeaveProject(Context.ConnectionId, id.Value);
        logger.LogDebug("Live group {Group} left by {ConnectionId}; members now {Count}",
            LiveGroups.Project(id.Value), Context.ConnectionId, registry.ProjectMemberCount(id.Value));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroups.Project(id.Value));
    }

    /// <summary>
    /// Joins a stream group and replays the journal's in-progress turn to the caller only. The
    /// group is joined first so nothing published during the replay is lost; a single event may
    /// then arrive twice around the handoff, which a client tolerates far better than a gap.
    /// </summary>
    public async Task JoinStream(string kind)
    {
        var group = StreamGroup(kind);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        registry.JoinStream(Context.ConnectionId, group);
        logger.LogDebug("Live group {Group} joined by {ConnectionId}; members now {Count}",
            group, Context.ConnectionId, registry.StreamMemberCount(group));

        if (group == LiveGroups.StreamLlm)
        {
            foreach (var message in LiveMessageMapper.Replay(Capture(llmJournal)))
                await Clients.Caller.Llm(message);
        }
        else
        {
            foreach (var e in Capture(audioJournal))
                await Clients.Caller.AudioGen(LiveMessageMapper.Map(e));
        }
    }

    public async Task LeaveStream(string kind)
    {
        var group = StreamGroup(kind);
        registry.LeaveStream(Context.ConnectionId, group);
        logger.LogDebug("Live group {Group} left by {ConnectionId}; members now {Count}",
            group, Context.ConnectionId, registry.StreamMemberCount(group));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>Singleton state plus a <see cref="ProjectSnapshot"/> for every project this connection joined.</summary>
    public LiveSnapshot GetSnapshot() => relay.BuildSnapshot(registry.FoldersOf(Context.ConnectionId));

    /// <summary>
    /// The journal's current turn, without staying subscribed: <c>Subscribe</c> replays then
    /// attaches atomically, and the immediate <c>Unsubscribe</c> detaches again.
    /// </summary>
    private static List<T> Capture<T>(EventJournal<T> journal)
    {
        var events = new List<T>();
        Action<T> handler = events.Add;
        journal.Subscribe(handler);
        journal.Unsubscribe(handler);
        return events;
    }

    private static ProjectFolderId ParseFolder(string folder) =>
        ProjectFolderId.TryParse(folder, out var id) ? id : throw new HubException($"Invalid project folder '{folder}'.");

    private static string StreamGroup(string kind) => kind switch
    {
        "llm" => LiveGroups.StreamLlm,
        "audio" => LiveGroups.StreamAudio,
        _ => throw new HubException($"Unknown stream '{kind}'. Use 'llm' or 'audio'."),
    };
}
