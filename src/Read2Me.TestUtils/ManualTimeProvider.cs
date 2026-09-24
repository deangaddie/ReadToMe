namespace Read2Me.TestUtils
{
    /// <summary>A clock that only moves when a test says so.</summary>
    public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
    {
        private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
