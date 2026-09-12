using System.Text;

namespace Read2Me.App.Live;

/// <summary>
/// The relay's rate-shaping rules (research/live-events.md §8) as pure state machines: every method
/// takes <c>now</c> so the drain loop drives them from its clock and tests from a hand-held one.
/// None of them sends anything; the relay asks <c>DueAt</c> to size its next wait and calls the
/// flush when the moment comes.
/// </summary>
public sealed class DeltaBatcher
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);

    private readonly StringBuilder _thinking = new();
    private readonly StringBuilder _content = new();
    private DateTimeOffset? _since;

    public bool HasPending => _since is not null;

    /// <summary>The instant the pending batch should go out, or null when nothing is pending.</summary>
    public DateTimeOffset? DueAt => _since + Window;

    public void Add(bool isThinking, string text, DateTimeOffset now)
    {
        (isThinking ? _thinking : _content).Append(text);
        _since ??= now;
    }

    /// <summary>
    /// Returns the batch when it is due (or <paramref name="force"/>d — e.g. a control event must
    /// not overtake the text that preceded it), otherwise null. Resets on return.
    /// </summary>
    public (string Thinking, string Content)? Flush(DateTimeOffset now, bool force = false)
    {
        if (_since is null || (!force && now < DueAt)) return null;
        var batch = (_thinking.ToString(), _content.ToString());
        _thinking.Clear();
        _content.Clear();
        _since = null;
        return batch;
    }
}

/// <summary>
/// Trailing-edge debounce: the first pulse starts a window, any number of further pulses ride it,
/// and one flush fires when the window closes. Latency is bounded by <see cref="Window"/> and the
/// rate by 1/<see cref="Window"/> however hard the source pulses.
/// </summary>
public sealed class Debouncer
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _window;
    private DateTimeOffset? _since;

    public Debouncer(TimeSpan? window = null) => _window = window ?? DefaultWindow;

    public bool IsPending => _since is not null;
    public DateTimeOffset? DueAt => _since + _window;

    public void Mark(DateTimeOffset now) => _since ??= now;

    /// <summary>True exactly once per window, when it has elapsed (or is forced).</summary>
    public bool TryFlush(DateTimeOffset now, bool force = false)
    {
        if (_since is null || (!force && now < DueAt)) return false;
        _since = null;
        return true;
    }
}

/// <summary>Passes an encode fraction through only when it moved at least one step since the last pass.</summary>
public sealed class ProgressStepper
{
    public const double DefaultStep = 0.01;

    private readonly double _step;
    private double? _last;

    public ProgressStepper(double step = DefaultStep) => _step = step;

    public bool Offer(double fraction)
    {
        if (_last is { } last && fraction < 1.0 && Math.Abs(fraction - last) < _step) return false;
        _last = fraction;
        return true;
    }

    /// <summary>A new run starts from nothing, so its first reading always passes.</summary>
    public void Reset() => _last = null;
}

/// <summary>
/// One throughput snapshot per second while a run is active, plus one final snapshot when the run
/// ends so the last figures are never a stale tick short.
/// </summary>
public sealed class ThroughputTicker
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private DateTimeOffset? _nextDue;
    private bool _finalPending;

    public bool IsTicking => _nextDue is not null;

    /// <summary>When the relay should wake for this ticker, or null when idle.</summary>
    public DateTimeOffset? DueAt => _finalPending ? DateTimeOffset.MinValue : _nextDue;

    public void RunStarted(DateTimeOffset now) => _nextDue ??= now + Interval;

    public void RunEnded()
    {
        _nextDue = null;
        _finalPending = true;
    }

    /// <summary>True when a snapshot should be sent now; schedules the next tick while running.</summary>
    public bool TryTick(DateTimeOffset now)
    {
        if (_finalPending)
        {
            _finalPending = false;
            return true;
        }
        if (_nextDue is not { } due || now < due) return false;
        _nextDue = now + Interval;
        return true;
    }
}
