using Xunit;

namespace CrimsonVR.Tests;

public sealed class RecenterHoldTests
{
    [Fact]
    public void TriggersOnlyAfterFullHold()
    {
        var hold = new RecenterHold();
        Assert.False(hold.Update(true, 100).Triggered);
        Assert.False(hold.Update(true, 799).Triggered);
        RecenterHoldState completed = hold.Update(true, 800);
        Assert.True(completed.Triggered);
        Assert.Equal(1.0f, completed.Progress);
    }

    [Fact]
    public void DoesNotRepeatUntilReleased()
    {
        var hold = new RecenterHold();
        hold.Update(true, 0);
        Assert.True(hold.Update(true, RecenterHold.DurationMs).Triggered);
        Assert.False(hold.Update(true, RecenterHold.DurationMs + 1000).Triggered);
        hold.Update(false, RecenterHold.DurationMs + 1001);
        hold.Update(true, RecenterHold.DurationMs + 1002);
        Assert.True(hold.Update(true, RecenterHold.DurationMs * 2 + 1002).Triggered);
    }

    [Fact]
    public void EarlyReleaseCancelsProgress()
    {
        var hold = new RecenterHold();
        hold.Update(true, 10);
        Assert.InRange(hold.Update(true, 300).Progress, 0.4f, 0.5f);
        Assert.False(hold.Update(false, 301).Holding);
        Assert.Equal(0.0f, hold.Update(true, 500).Progress);
    }
}
