namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>What moved the Setup check report: a Full check's progress, or its end.</summary>
    public enum SetupCheckChangeKind
    {
        Progress,
        Done,
    }

    /// <summary>
    /// Published on the singleton <c>EventBroadcaster&lt;SetupCheckChanged&gt;</c> each time a Full check
    /// moves the report, in the order it moved; the live relay sends each to every client.
    /// </summary>
    public sealed record SetupCheckChanged(SetupCheckChangeKind Kind, SetupCheckReport Report);
}
