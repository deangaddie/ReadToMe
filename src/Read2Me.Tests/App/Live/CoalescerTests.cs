using Read2Me.App.Live;
using Xunit;

namespace Read2Me.Tests.App.Live;

public class CoalescerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeltaBatcher_holds_text_until_the_window_closes_then_returns_one_batch()
    {
        var batcher = new DeltaBatcher();
        batcher.Add(isThinking: true, "hm", T0);
        batcher.Add(isThinking: false, "Hello", T0.AddMilliseconds(20));
        batcher.Add(isThinking: false, " world", T0.AddMilliseconds(40));

        Assert.Equal(T0 + DeltaBatcher.Window, batcher.DueAt);
        Assert.Null(batcher.Flush(T0.AddMilliseconds(99)));

        var batch = batcher.Flush(T0.AddMilliseconds(100));
        Assert.Equal(("hm", "Hello world"), batch);
        Assert.False(batcher.HasPending);
        Assert.Null(batcher.DueAt);
    }

    [Fact]
    public void DeltaBatcher_force_flush_returns_pending_text_early_and_nothing_when_empty()
    {
        var batcher = new DeltaBatcher();
        Assert.Null(batcher.Flush(T0, force: true));

        batcher.Add(isThinking: false, "x", T0);
        Assert.Equal(("", "x"), batcher.Flush(T0.AddMilliseconds(1), force: true));
    }

    [Fact]
    public void Debouncer_fires_once_per_window_however_many_pulses_arrive()
    {
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(250));
        for (var i = 0; i < 100; i++)
            debouncer.Mark(T0.AddMilliseconds(i * 2));

        Assert.Equal(T0.AddMilliseconds(250), debouncer.DueAt);
        Assert.False(debouncer.TryFlush(T0.AddMilliseconds(249)));
        Assert.True(debouncer.TryFlush(T0.AddMilliseconds(250)));
        Assert.False(debouncer.TryFlush(T0.AddMilliseconds(251)));
        Assert.False(debouncer.IsPending);
    }

    [Fact]
    public void ProgressStepper_passes_first_reading_then_only_whole_steps_and_completion()
    {
        var stepper = new ProgressStepper(0.01);
        Assert.True(stepper.Offer(0.0));
        Assert.False(stepper.Offer(0.004));
        Assert.False(stepper.Offer(0.009));
        Assert.True(stepper.Offer(0.010));
        Assert.False(stepper.Offer(0.015));
        Assert.True(stepper.Offer(1.0));

        stepper.Reset();
        Assert.True(stepper.Offer(0.0));
    }

    [Fact]
    public void ThroughputTicker_ticks_once_per_second_while_running_and_once_more_on_end()
    {
        var ticker = new ThroughputTicker();
        Assert.Null(ticker.DueAt);
        Assert.False(ticker.TryTick(T0));

        ticker.RunStarted(T0);
        Assert.Equal(T0.AddSeconds(1), ticker.DueAt);
        Assert.False(ticker.TryTick(T0.AddMilliseconds(999)));
        Assert.True(ticker.TryTick(T0.AddSeconds(1)));
        Assert.False(ticker.TryTick(T0.AddSeconds(1.5)));
        Assert.True(ticker.TryTick(T0.AddSeconds(2)));

        ticker.RunEnded();
        Assert.True(ticker.DueAt <= T0);
        Assert.True(ticker.TryTick(T0.AddSeconds(2.1)));
        Assert.False(ticker.TryTick(T0.AddSeconds(5)));
        Assert.Null(ticker.DueAt);
    }
}
