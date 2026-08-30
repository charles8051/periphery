using System.Collections.Generic;
using System.Linq;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Hardware-free regression coverage for issue #231: Windows re-reports the same
/// physical device instance id with different <b>casing</b> across a re-enumeration.
/// Gate tier (no <c>Category=Integration</c> trait) so CI catches a re-introduction.
///
/// <para>Fixtures are the real pair observed on this bench across a board reset, 224 ms
/// apart, same board and same port:
/// <c>USB\VID_10C4&amp;PID_8A7E\CDYHINBH</c> -> <c>USB\VID_10C4&amp;PID_8A7E\cDYhINBh</c>.</para>
///
/// <para>Autoflash makes this safety-relevant rather than cosmetic: a bootloader-mode
/// device that returns in different casing must resolve to the row that was already
/// flashed this session, or it is flashed a <b>second</b> time unattended.</para>
/// </summary>
public class TargetIdentityCaseTests
{
    private const string FirstCase  = @"USB\VID_10C4&PID_8A7E\CDYHINBH";
    private const string SecondCase = @"USB\VID_10C4&PID_8A7E\cDYhINBh";
    private const string Family = "STM32 USB DFU";

    private static readonly AutoflashConfig Armed = new(Family, FlashOptions.Default);

    private static FlashTargetView Target(DeviceId id) => new(id, id.Value, Family);

    [Fact]
    public void TheUnderlyingStrings_ReallyDoDiffer_SoTheTypeIsLoadBearing()
    {
        Assert.NotEqual(FirstCase, SecondCase, StringComparer.Ordinal);
        Assert.Equal(FirstCase, SecondCase, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Autoflash_DoesNotReflashADeviceThatReturnsInDifferentCase()
    {
        // The already-flashed set is the ONLY thing enforcing once-per-armed-session.
        // A plain HashSet<string> here would miss and flash the board a second time.
        IReadOnlySet<DeviceId> flashed = new HashSet<DeviceId> { FirstCase };

        var action = AutoflashPolicy.Decide(Armed, Target(SecondCase), flashed);

        Assert.Contains("already flashed", Assert.IsType<AutoflashAction.Skip>(action).Reason);
    }

    [Fact]
    public void Rediscovery_InDifferentCase_UpdatesTheSameRow_RatherThanAddingASecond()
    {
        var s = AppReducer.ReduceAll(
            AppState.Empty,
            new AppEvent.TargetDetected(FirstCase, "board", Family),
            new AppEvent.TargetDetected(SecondCase, "board (returned)", Family));

        var target = Assert.Single(s.Targets);
        Assert.Equal("board (returned)", target.DisplayName);
    }

    [Fact]
    public void Removal_InDifferentCase_RemovesTheTarget()
    {
        var s = AppReducer.ReduceAll(
            AppState.Empty,
            new AppEvent.TargetDetected(FirstCase, "board", Family),
            new AppEvent.TargetRemoved(SecondCase));

        Assert.Empty(s.Targets);
    }

    [Fact]
    public void CliTargetSelection_MatchesAcrossTheCaseDivergence()
    {
        // The `--target <id>` shape in Periphery.FlashAnything.Cli.Core: an id the
        // operator copied before a reset must still select the board after it.
        var s = AppReducer.ReduceAll(
            AppState.Empty, new AppEvent.TargetDetected(FirstCase, "board", Family));

        DeviceId requested = SecondCase;
        var selected = s.Targets.Where(t => t.Id == requested).ToList();

        Assert.Single(selected);
    }
}
