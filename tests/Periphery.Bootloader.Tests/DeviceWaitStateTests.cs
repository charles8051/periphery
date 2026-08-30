using System;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Unit tests for the pure wait/correlation core (<see cref="DeviceWaitState"/>) that
/// <see cref="BootloaderEntryOrchestrator"/> advances. No hardware, no watcher, no clock — just the
/// state machine (ADR-0052 functional core / ADR-0063 DEC-005 correlation policy).
/// </summary>
public class DeviceWaitStateTests
{
    private static DeviceInfo Dev(
        string id, string? serial = null, ushort vid = 0x10C4, ushort pid = 0xEAC9, string? location = null)
        => new()
        {
            Id = id,
            VendorId = new HardwareId(vid),
            ProductId = new HardwareId(pid),
            SerialNumber = serial,
            LocationPath = location,
            IsActive = true,
        };

    // ── FirstAppearance with debounce — the bootloader re-enumeration wait ──────────────────────

    [Fact]
    public void Bootloader_FreshAppearanceAfterArm_Correlates()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .Arm()                       // nothing pre-existing
            .OnAppeared(Dev("boot-1"));  // the reboot's bootloader appears

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("boot-1", s.Correlated!.Id);
    }

    [Fact]
    public void Bootloader_PreExistingIsDebounced_OnlyFreshWins()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .OnAppeared(Dev("stale-bootloader"))   // a bystander EFM8 bootloader already on the bus
            .Arm();                                 // freezes it into the debounce baseline

        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);   // the bystander does NOT correlate

        s = s.OnAppeared(Dev("stale-bootloader"));          // even if it re-raises an event
        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);

        s = s.OnAppeared(Dev("our-bootloader"));            // our reboot's device is fresh
        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("our-bootloader", s.Correlated!.Id);
    }

    [Fact]
    public void Bootloader_PreExistingThatReEnumerates_CountsAsFresh()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .OnAppeared(Dev("dev-A"))
            .Arm()                          // dev-A is in the debounce baseline
            .OnDisappeared("dev-A")         // it drops off the bus (re-enumerating)
            .OnAppeared(Dev("dev-A"));      // and comes back — now a genuine fresh appearance

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("dev-A", s.Correlated!.Id);
    }

    [Fact]
    public void Bootloader_NeverAppears_TimesOut()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .OnAppeared(Dev("stale"))
            .Arm()
            .OnTimeout();

        Assert.Equal(DeviceWaitStatus.TimedOut, s.Status);
        Assert.Null(s.Correlated);
    }

    // ── FirstAppearance without debounce — the application-liveness wait ────────────────────────

    [Fact]
    public void AppLiveness_PreExistingMatch_CorrelatesAtArm()
    {
        // The just-flashed app may already be back by the time we look — accept it.
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: false)
            .OnAppeared(Dev("app-1", vid: 0x10C4, pid: 0x8A7E))
            .Arm();

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("app-1", s.Correlated!.Id);
    }

    [Fact]
    public void AppLiveness_NoneYet_ThenFreshAppearanceCorrelates()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: false)
            .Arm();                         // nothing present yet
        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);

        s = s.OnAppeared(Dev("app-1", vid: 0x10C4, pid: 0x8A7E));
        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("app-1", s.Correlated!.Id);
    }

    // ── BySerial — exact correlation that survives the mode switch ──────────────────────────────

    [Fact]
    public void BySerial_MatchingSerialWins_NonMatchingIgnored()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.BySerial, debouncePreExisting: true, expectedSerial: "SN-42")
            .Arm()
            .OnAppeared(Dev("other", serial: "SN-99"));   // wrong serial
        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);

        s = s.OnAppeared(Dev("ours", serial: "SN-42"));   // right serial
        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("ours", s.Correlated!.Id);
    }

    [Fact]
    public void BySerial_PreExistingMatch_CorrelatesAtArm()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.BySerial, debouncePreExisting: true, expectedSerial: "SN-42")
            .OnAppeared(Dev("ours", serial: "SN-42"))
            .Arm();

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("ours", s.Correlated!.Id);
    }

    [Fact]
    public void BySerial_IsCaseInsensitive()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.BySerial, debouncePreExisting: true, expectedSerial: "abc123")
            .Arm()
            .OnAppeared(Dev("ours", serial: "ABC123"));

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
    }

    [Fact]
    public void BySerial_NoMatch_TimesOut()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.BySerial, debouncePreExisting: true, expectedSerial: "SN-42")
            .Arm()
            .OnAppeared(Dev("other", serial: "SN-99"))
            .OnTimeout();

        Assert.Equal(DeviceWaitStatus.TimedOut, s.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BySerial_WithoutExpectedSerial_Throws(string? serial)
    {
        Assert.Throws<ArgumentException>(() =>
            DeviceWaitState.Collecting(DeviceCorrelationMode.BySerial, debouncePreExisting: true, expectedSerial: serial));
    }

    // ── ByLocationPath — exact correlation by the USB port that survives the mode switch ─────────

    private const string PortA = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(3)";
    private const string PortB = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(4)";

    [Fact]
    public void ByLocationPath_MatchingPortWins_NonMatchingIgnored()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortA)
            .Arm()
            .OnAppeared(Dev("other-port", location: PortB));   // a different board rebooting on another port
        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);      // not ours — ignored regardless of appearing first

        s = s.OnAppeared(Dev("ours", location: PortA));        // our board's bootloader, on our port
        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("ours", s.Correlated!.Id);
    }

    [Fact]
    public void ByLocationPath_MatchesRegardlessOfAppearanceOrder()
    {
        // The whole point vs. FirstAppearance: a bootloader on a DIFFERENT port appearing FIRST must not
        // steal this wait — identity, not timing, decides. (This is the concurrency-collapse regression.)
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortB)
            .Arm()
            .OnAppeared(Dev("first-but-wrong", location: PortA))   // appears first, wrong port
            .OnAppeared(Dev("second-and-right", location: PortB)); // appears second, our port

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("second-and-right", s.Correlated!.Id);
    }

    [Fact]
    public void ByLocationPath_PreExistingMatch_CorrelatesAtArm()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortA)
            .OnAppeared(Dev("ours", location: PortA))
            .Arm();

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
        Assert.Equal("ours", s.Correlated!.Id);
    }

    [Fact]
    public void ByLocationPath_IsCaseInsensitive()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortA.ToLowerInvariant())
            .Arm()
            .OnAppeared(Dev("ours", location: PortA.ToUpperInvariant()));

        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);
    }

    [Fact]
    public void ByLocationPath_NoMatch_TimesOut()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortA)
            .Arm()
            .OnAppeared(Dev("other", location: PortB))
            .OnTimeout();

        Assert.Equal(DeviceWaitStatus.TimedOut, s.Status);
    }

    [Fact]
    public void ByLocationPath_CandidateWithoutLocation_DoesNotMatch()
    {
        // A candidate exposing no port can never be the one we correlate on port; it must not match
        // (in particular it must not match an expected value via a null==null slip).
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: PortA)
            .Arm()
            .OnAppeared(Dev("no-port", location: null));

        Assert.Equal(DeviceWaitStatus.Waiting, s.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]   // whitespace-only can never match a real port — rejected like empty
    public void ByLocationPath_WithoutExpectedLocation_Throws(string? location)
    {
        Assert.Throws<ArgumentException>(() =>
            DeviceWaitState.Collecting(DeviceCorrelationMode.ByLocationPath, debouncePreExisting: true, expectedLocationPath: location));
    }

    // ── Phase discipline & terminal idempotency ────────────────────────────────────────────────

    [Fact]
    public void Collecting_DoesNotCorrelate_BeforeArm()
    {
        // Even with debounce off, an appearance during the collect phase only accumulates.
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: false)
            .OnAppeared(Dev("app-1"));

        Assert.Equal(DeviceWaitStatus.Collecting, s.Status);
        Assert.Null(s.Correlated);
    }

    [Fact]
    public void Correlated_IsNotOverriddenByTimeout()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .Arm()
            .OnAppeared(Dev("boot-1"));
        Assert.Equal(DeviceWaitStatus.Correlated, s.Status);

        var after = s.OnTimeout();
        Assert.Equal(DeviceWaitStatus.Correlated, after.Status);
        Assert.Equal("boot-1", after.Correlated!.Id);
    }

    [Fact]
    public void TimedOut_IsNotOverriddenByAppearance()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true)
            .Arm()
            .OnTimeout();
        Assert.Equal(DeviceWaitStatus.TimedOut, s.Status);

        var after = s.OnAppeared(Dev("late"));
        Assert.Equal(DeviceWaitStatus.TimedOut, after.Status);
        Assert.Null(after.Correlated);
    }

    [Fact]
    public void Arm_IsNoOp_WhenAlreadyArmedOrTerminal()
    {
        var waiting = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true).Arm();
        Assert.Same(waiting, waiting.Arm());

        var correlated = waiting.OnAppeared(Dev("boot-1"));
        Assert.Equal(DeviceWaitStatus.Correlated, correlated.Status);
        Assert.Same(correlated, correlated.Arm());
    }

    [Fact]
    public void Transitions_AreImmutable()
    {
        var collecting = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true);
        var armed = collecting.Arm();
        var correlated = armed.OnAppeared(Dev("boot-1"));

        // Earlier states are untouched by later transitions.
        Assert.Equal(DeviceWaitStatus.Collecting, collecting.Status);
        Assert.Equal(DeviceWaitStatus.Waiting, armed.Status);
        Assert.Equal(DeviceWaitStatus.Correlated, correlated.Status);
    }

    [Fact]
    public void DisappearanceOfUnknownDevice_IsHarmless()
    {
        var s = DeviceWaitState.Collecting(DeviceCorrelationMode.FirstAppearance, debouncePreExisting: true).Arm();
        var after = s.OnDisappeared("never-seen");
        Assert.Same(s, after);   // no state churn
    }
}
