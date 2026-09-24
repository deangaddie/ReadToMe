using Microsoft.AspNetCore.SignalR;
using Read2Me.Services.Llm;

namespace Read2Me.App.Live;

/// <summary>
/// Strongly-typed server→client surface of <see cref="LiveHub"/>. The wire method name is the
/// lower-camel event family from the spec contract (<c>connection.on('queue', …)</c>), pinned with
/// <see cref="HubMethodNameAttribute"/> so neither the C# names nor a client's case rules leak in.
/// </summary>
public interface ILiveClient
{
    [HubMethodName("queue")]
    Task Queue(QueueMessage message);
    [HubMethodName("nodeStatus")]
    Task NodeStatus(NodeStatusMessage message);
    [HubMethodName("itemStatus")]
    Task ItemStatus(ItemStatusMessage message);
    [HubMethodName("receipt")]
    Task Receipt(ReceiptMessage message);
    [HubMethodName("assembly")]
    Task Assembly(AssemblyMessage message);
    [HubMethodName("voiceBatch")]
    Task VoiceBatch(VoiceBatchMessage message);
    [HubMethodName("watchdog")]
    Task Watchdog(WatchdogMessage message);
    [HubMethodName("serviceStatus")]
    Task ServiceStatus(ServiceStatusMessage message);
    [HubMethodName("preflight")]
    Task Preflight(PreflightMessage message);
    [HubMethodName("llm")]
    Task Llm(LlmMessage message);
    [HubMethodName("audioGen")]
    Task AudioGen(AudioGenMessage message);
    [HubMethodName("throughput")]
    Task Throughput(ThroughputSnapshot snapshot);
    [HubMethodName("settingsChanged")]
    Task SettingsChanged(SettingsChangedMessage message);
    [HubMethodName("bookEdit")]
    Task BookEdit(BookEditMessage message);
    [HubMethodName("llmTest")]
    Task LlmTest(LlmTestMessage message);
}
