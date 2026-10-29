using Microsoft.AspNetCore.SignalR;
using Read2Me.App.Live;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Health;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What the Full check drives on top of the Quick check's reads: both queues' processing gates
    /// (closed for the run), the work it refuses around, the busy flag Preflight reads, the watchdog
    /// it holds off, the ffmpeg encode it runs and the live hub its progress goes out on.
    /// </summary>
    public sealed record FullCheckServices(
        IProcessingGate<QueuedParagraph> CharacterGate,
        IProcessingGate<QueuedAudioItem> AudioGate,
        InFlightWork Work,
        SetupCheckBusy Busy,
        IWatchdogHoldOff Watchdog,
        IFfmpegEncodeCheck Encode,
        IHubContext<LiveHub, ILiveClient> Hub);
}
