namespace Periphery.Tests;

/// <summary>
/// Pure-value tests for <see cref="EnricherScope"/> — the per-platform OS
/// enumeration tokens a tag-emitting enricher declares (ADR-0051 §5).
/// </summary>
public class EnricherScopeTests
{
    [Fact]
    public void None_IsEmptyOnEveryPlatform()
    {
        Assert.True(EnricherScope.None.IsEmpty);
        Assert.Empty(EnricherScope.None.WindowsClassGuids);
        Assert.Empty(EnricherScope.None.LinuxSubsystems);
        Assert.Empty(EnricherScope.None.MacOSClasses);
    }

    [Fact]
    public void IsEmpty_FalseWhenAnyPlatformHasTokens()
    {
        Assert.False(new EnricherScope(["{guid}"], [], []).IsEmpty);
        Assert.False(new EnricherScope([], ["tty"], []).IsEmpty);
        Assert.False(new EnricherScope([], [], ["IOSerialBSDClient"]).IsEmpty);
    }
}
