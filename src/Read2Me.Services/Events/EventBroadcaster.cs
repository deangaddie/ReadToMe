namespace Read2Me.Services.Events;

/// Singleton bridge: a scoped producer publishes; a singleton-subscribed view receives.
/// One transport, many event families. Replaces per-feature broadcaster shells.
public sealed class EventBroadcaster<T>
{
    public event Action<T>? Event;
    public void Publish(T e) => Event?.Invoke(e);
}
