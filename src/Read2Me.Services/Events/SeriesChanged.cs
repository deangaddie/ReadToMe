namespace Read2Me.Services.Events;

/// <summary>
/// A series registry was written, or a series deleted (#213). Published on the singleton
/// <c>EventBroadcaster&lt;SeriesChanged&gt;</c>; the live hub relay turns it into the debounced
/// <c>series</c> message, a "read again" signal with no payload.
/// </summary>
public sealed record SeriesChanged(Guid SeriesId, SeriesChangeKind Kind);

public enum SeriesChangeKind
{
    Changed,
    Deleted,
}
