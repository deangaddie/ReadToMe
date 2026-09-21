using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Read2Me.App.Live;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// An <see cref="IHubContext{THub,T}"/> that records every send as (target group, method, payload).
/// Setting <see cref="Gate"/> makes every send await it, which is how a test pins the relay's drain
/// loop to prove publishers never wait on it.
/// </summary>
public sealed class RecordingHubContext : IHubContext<LiveHub, ILiveClient>
{
    public sealed record Sent(string Target, string Method, object Payload);

    public ConcurrentQueue<Sent> Messages { get; } = new();
    public TaskCompletionSource? Gate { get; set; }

    public IHubClients<ILiveClient> Clients => new RecordingClients(this);
    public IGroupManager Groups { get; } = Substitute.For<IGroupManager>();

    public IReadOnlyList<Sent> To(string target) => Messages.Where(m => m.Target == target).ToList();
    public IReadOnlyList<Sent> Method(string method) => Messages.Where(m => m.Method == method).ToList();

    private sealed class RecordingClients(RecordingHubContext owner) : IHubClients<ILiveClient>
    {
        public ILiveClient All => new RecordingClient(owner, "all");
        public ILiveClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(owner, "all");
        public ILiveClient Client(string connectionId) => new RecordingClient(owner, $"client:{connectionId}");
        public ILiveClient Clients(IReadOnlyList<string> connectionIds) => new RecordingClient(owner, "clients");
        public ILiveClient Group(string groupName) => new RecordingClient(owner, groupName);
        public ILiveClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClient(owner, groupName);
        public ILiveClient Groups(IReadOnlyList<string> groupNames) => new RecordingClient(owner, string.Join(",", groupNames));
        public ILiveClient User(string userId) => new RecordingClient(owner, $"user:{userId}");
        public ILiveClient Users(IReadOnlyList<string> userIds) => new RecordingClient(owner, "users");
    }

    private sealed class RecordingClient(RecordingHubContext owner, string target) : ILiveClient
    {
        private Task Record(string method, object payload)
        {
            owner.Messages.Enqueue(new Sent(target, method, payload));
            return owner.Gate?.Task ?? Task.CompletedTask;
        }

        public Task Queue(QueueMessage message) => Record("queue", message);
        public Task NodeStatus(NodeStatusMessage message) => Record("nodeStatus", message);
        public Task ItemStatus(ItemStatusMessage message) => Record("itemStatus", message);
        public Task Receipt(ReceiptMessage message) => Record("receipt", message);
        public Task Assembly(AssemblyMessage message) => Record("assembly", message);
        public Task VoiceBatch(VoiceBatchMessage message) => Record("voiceBatch", message);
        public Task Watchdog(WatchdogMessage message) => Record("watchdog", message);
        public Task Llm(LlmMessage message) => Record("llm", message);
        public Task AudioGen(AudioGenMessage message) => Record("audioGen", message);
        public Task Throughput(ThroughputSnapshot snapshot) => Record("throughput", snapshot);
        public Task SettingsChanged(SettingsChangedMessage message) => Record("settingsChanged", message);
        public Task BookEdit(BookEditMessage message) => Record("bookEdit", message);
        public Task LlmTest(LlmTestMessage message) => Record("llmTest", message);
    }
}
