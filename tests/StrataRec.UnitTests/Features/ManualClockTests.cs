namespace StrataRec.UnitTests.Features;

/// <summary>The tests' clock (<see cref="ManualClock"/>) does what a test says on any machine.</summary>
public sealed class ManualClockTests
{
    /// <summary>
    /// Held timestamps stand still wherever a test moves them. A timestamp counts from when the machine started, and
    /// the clock took a negative one for "not held": on a CI runner, started minutes before, the ten minutes two
    /// BoundedCall tests move their clock back gave real timestamps again, and both tests failed there, and only there.
    /// </summary>
    [Fact]
    public void HeldTimestamps_MovedBackPastTheMachinesStart_StillStandStill()
    {
        var clock = new ManualClock();
        clock.HoldTimestamps();
        var held = clock.GetTimestamp();

        clock.AdvanceTimestamps(-TimeSpan.FromDays(3650));

        var moved = clock.GetTimestamp();
        Assert.Equal(TimeSpan.FromDays(3650), clock.GetElapsedTime(moved, held));
        Assert.True(moved < 0, "the machine has been up for ten years");
        Assert.Equal(moved, clock.GetTimestamp());
    }
}
