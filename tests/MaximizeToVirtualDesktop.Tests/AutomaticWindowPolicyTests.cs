using Xunit;

namespace MaximizeToVirtualDesktop.Tests;

public sealed class AutomaticWindowPolicyTests
{
    [Fact]
    public void ShiftBypassSurvivesRepeatedMaximizeEventsUntilRestore()
    {
        var policy = new AutomaticWindowPolicy();
        policy.Bypass(1);
        var maximized = new WindowState(true, false, false, true);
        Assert.Equal(AutomaticWindowAction.None, policy.Decide(1, maximized, true));
        Assert.Equal(AutomaticWindowAction.None, policy.Decide(1, maximized, true));
        policy.Decide(1, maximized with { Maximized = false }, true);
        Assert.Equal(AutomaticWindowAction.Enter, policy.Decide(1, maximized, true));
    }

    [Fact]
    public void BypassingOneWindowDoesNotAffectAnother()
    {
        var policy = new AutomaticWindowPolicy();
        policy.Bypass(1);
        Assert.Equal(AutomaticWindowAction.Enter, policy.Decide(2, new(true, false, false, true), true));
    }

    [Fact]
    public void ClosingWindowDiscardsItsBypassBeforeHandleReuse()
    {
        var policy = new AutomaticWindowPolicy();
        policy.Bypass(1);
        policy.Forget(1);
        Assert.Equal(AutomaticWindowAction.Enter, policy.Decide(1, new(true, false, false, true), true));
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, true, false, true, true)]
    public void UnchangedOrIneligibleWindowsAreLeftAlone(bool maximized, bool tracked, bool busy, bool eligible, bool enabled)
    {
        Assert.Equal(AutomaticWindowAction.None,
            new AutomaticWindowPolicy().Decide(1, new(maximized, tracked, busy, eligible), enabled));
    }

    [Fact]
    public void TurningAutomaticModeOffStillAllowsTrackedWindowCleanup()
    {
        Assert.Equal(AutomaticWindowAction.Restore,
            new AutomaticWindowPolicy().Decide(1, new(false, true, false, false), false));
    }
}
