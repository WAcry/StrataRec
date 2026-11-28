namespace StrataRec.UnitTests.Features;

public sealed class ManualClockTests
{
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
