namespace Read2Me.App.Characters;

/// <summary>
/// Counts the character discovery requests in flight. Discovery answers synchronously, so nothing
/// else knows one is running; the Setup check's Full check reads this to refuse while one is.
/// </summary>
public sealed class DiscoveryRuns
{
    private int _running;

    public bool Running => Volatile.Read(ref _running) > 0;

    /// <summary>Marks one discovery running until the handle is disposed.</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _running);
        return new End(this);
    }

    private sealed class End(DiscoveryRuns runs) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref runs._running);
        }
    }
}
