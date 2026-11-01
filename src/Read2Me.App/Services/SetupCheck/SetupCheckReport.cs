using System.Text.Json.Serialization;
using Read2Me.App.Api;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>The area a check sits under in the report's one table.</summary>
    public enum SetupCheckGroup
    {
        WorkspaceAndTools,
        AiConfigs,
        DockerServices,
    }

    /// <summary>Which check judges the row: the Quick check reads, the Full check sends real requests.</summary>
    public enum SetupCheckTier
    {
        Quick,
        Full,
    }

    /// <summary>A check's one result. <see cref="Checking"/> is transient, while a run is on that row.</summary>
    public enum SetupCheckResult
    {
        Passed,
        Failed,
        NotConfirmed,
        NotRun,
        NotUsed,
        Checking,
    }

    /// <summary>Where a Full check step runs: on this machine, in a CPU container, or in the one GPU container that fits.</summary>
    public enum SetupCheckLane
    {
        Local,
        Cpu,
        Gpu,
    }

    /// <summary>
    /// The Full check going on: step <see cref="Step"/> of <see cref="Total"/>, its lane and what it is
    /// doing ("Encoding a 1 s tone to .m4b"). <see cref="Cancelling"/>: a cancel was asked for, so the
    /// run stops once this step ends.
    /// </summary>
    public sealed record SetupFullRun(
        int Step,
        int Total,
        SetupCheckLane Lane,
        string Label,
        bool Cancelling);

    /// <summary>The in-app Settings section that owns the value: an app-relative path and its label.</summary>
    public sealed record SetupSettingsLink(string Path, string Label)
    {
        internal static readonly SetupSettingsLink Llm = new("settings/llm", "Settings → LLM");
        internal static readonly SetupSettingsLink Tts = new("settings/tts", "Settings → Paragraph TTS");
        internal static readonly SetupSettingsLink VoiceDesign = new("settings/voice-design", "Settings → Voice design");
        internal static readonly SetupSettingsLink Transcription = new("settings/transcription", "Settings → Transcription");
        internal static readonly SetupSettingsLink Similarity = new("settings/similarity", "Settings → Similarity");
    }

    /// <summary>
    /// Where to go next: one sentence, plus a link to the Settings section that owns the value and/or
    /// a command or file path to copy, for things outside the app; null when there is none.
    /// </summary>
    public sealed record SetupFixHint(string Text, SetupSettingsLink? Link, string? Copy);

    /// <summary>
    /// One check: what the app expected and what it found. A <see cref="Note"/> never changes the
    /// result. Every row carries a fix hint; the page shows it only for rows that aren't Passed or Not used.
    /// <see cref="Managed"/>: the app can start and restart what the row checks — a docker AI service,
    /// or a config whose every URL is one. False for the Workspace and tools, and for a config on a
    /// URL the app doesn't manage. <see cref="Rerun"/>: what re-running this one check takes, null
    /// when it can't be (a Quick-tier row, or a config with no active one).
    /// </summary>
    public sealed record SetupCheckRow(
        string Id,
        SetupCheckGroup Group,
        string Name,
        SetupCheckTier Tier,
        SetupCheckResult Result,
        string Expected,
        string Found,
        string? Note,
        SetupFixHint Fix,
        bool Managed,
        SetupRerun? Rerun);

    /// <summary>
    /// Re-running one Full-tier check. <see cref="Gpu"/>: it drives a GPU container, so it stops the
    /// one running and loads a model, and the web app confirms first.
    /// </summary>
    public sealed record SetupRerun(bool Gpu);

    /// <summary>
    /// The Setup check report: the rows in table order and when each tier last ran (null: not yet).
    /// <see cref="FirstRun"/>: this start created app.db, so the web app opens on the Setup check.
    /// <see cref="StartedAt"/>: when this host started; the start banner's dismissal lasts for it.
    /// <see cref="NoWorkspace"/>: this host started without a usable Workspace; every other API call
    /// is refused and the web app shows only the Setup check until the app restarts.
    /// <see cref="FullRun"/>: the Full check going on, null when none is. <see cref="FullCheckEstimateSeconds"/>:
    /// how long a Full check is likely to take, for its confirm. <see cref="PutBack"/>: after a Full
    /// check, what it did with the containers it drove ("Put back: read2me-whisper stopped again.").
    /// </summary>
    public sealed record SetupCheckReport(
        IReadOnlyList<SetupCheckRow> Rows,
        DateTimeOffset? QuickCheckedAt,
        DateTimeOffset? FullCheckedAt,
        bool FirstRun,
        DateTimeOffset StartedAt,
        bool NoWorkspace,
        SetupFullRun? FullRun,
        int FullCheckEstimateSeconds,
        string? PutBack)
    {
        /// <summary>No rows yet; the runner stamps the start's flags and time on as it answers.</summary>
        public static readonly SetupCheckReport Empty = new([], null, null, false, default, false, null, 0, null);
    }
}
