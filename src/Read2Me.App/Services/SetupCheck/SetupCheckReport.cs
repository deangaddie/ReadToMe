using System.Text.Json.Serialization;
using Read2Me.App.Api;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>The area a check sits under in the report's one table.</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter<SetupCheckGroup>))]
    public enum SetupCheckGroup
    {
        WorkspaceAndTools,
        AiConfigs,
        DockerServices,
    }

    /// <summary>Which check judges the row: the Quick check reads, the Full check sends real requests.</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter<SetupCheckTier>))]
    public enum SetupCheckTier
    {
        Quick,
        Full,
    }

    /// <summary>A check's one result. <see cref="Checking"/> is transient, while a run is on that row.</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter<SetupCheckResult>))]
    public enum SetupCheckResult
    {
        Passed,
        Failed,
        NotConfirmed,
        NotRun,
        NotUsed,
        Checking,
    }

    /// <summary>The in-app Settings section that owns the value: an app-relative path and its label.</summary>
    public sealed record SetupSettingsLink(string Path, string Label);

    /// <summary>
    /// Where to go next: one sentence, plus a link to the Settings section that owns the value and/or
    /// a command or file path to copy, for things outside the app.
    /// </summary>
    public sealed record SetupFixHint(string Text, SetupSettingsLink? Link = null, string? Copy = null);

    /// <summary>
    /// One check: what the app expected and what it found. A <see cref="Note"/> never changes the
    /// result. Every row carries a fix hint; the page shows it only for rows that aren't Passed or Not used.
    /// <see cref="Managed"/>: the app can start and restart what the row checks — a docker AI service,
    /// or a config whose every URL is one. False for the Workspace and tools, and for a config on a
    /// URL the app doesn't manage.
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
        bool Managed);

    /// <summary>
    /// The Setup check report: the rows in table order and when each tier last ran (null: not yet).
    /// <see cref="FirstRun"/>: this start created app.db, so the web app opens on the Setup check.
    /// <see cref="StartedAt"/>: when this host started; the start banner's dismissal lasts for it.
    /// </summary>
    public sealed record SetupCheckReport(
        IReadOnlyList<SetupCheckRow> Rows,
        DateTimeOffset? QuickCheckedAt,
        DateTimeOffset? FullCheckedAt,
        bool FirstRun,
        DateTimeOffset StartedAt)
    {
        /// <summary>No rows yet; the runner stamps the first-run flag and start time on as it answers.</summary>
        public static readonly SetupCheckReport Empty = new([], null, null, false, default);
    }
}
