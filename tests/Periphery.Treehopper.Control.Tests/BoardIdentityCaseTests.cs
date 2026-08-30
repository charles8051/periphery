using System.Linq;
using Periphery.Treehopper;
using Periphery.Treehopper.Control;
using Xunit;

namespace Periphery.Treehopper.Control.Tests;

/// <summary>
/// Hardware-free regression coverage for issue #231: Windows re-reports the same
/// physical device instance id with different <b>casing</b> across a re-enumeration.
/// Runs in the <b>gate tier</b> (no <c>Category=Integration</c> trait) so CI catches a
/// re-introduction on every build.
///
/// <para>The strings below are the real pair observed on this bench across a board
/// reset, 224 ms apart, on the same board and the same port:</para>
/// <code>
/// 22:26:08.016  Device disappeared: USB\VID_10C4&amp;PID_8A7E\CDYHINBH
/// 22:26:08.240  Device enumerated:  USB\VID_10C4&amp;PID_8A7E\cDYhINBh
/// </code>
///
/// <para>What this pins: the reducer's board identity must join across that
/// divergence <b>because the ids are typed</b> <see cref="Periphery.DeviceId"/>, whose
/// equality is <see cref="System.StringComparison.OrdinalIgnoreCase"/>. Revert
/// <c>BoardView.Id</c> / <c>BoardIdentity.Id</c> / <c>AppState.SelectedBoardId</c> to
/// <c>string</c> and these fail immediately and locally.</para>
/// </summary>
public class BoardIdentityCaseTests
{
    private const string FirstCase  = @"USB\VID_10C4&PID_8A7E\CDYHINBH";
    private const string SecondCase = @"USB\VID_10C4&PID_8A7E\cDYhINBh";

    private static BoardIdentity Id(string id, string? serial = null, int? version = null,
        BoardConnection conn = BoardConnection.Application)
        => new(id, serial, Name: null, Version: version, Connection: conn);

    private static AppState With(params AppEvent[] events) => AppReducer.ReduceAll(AppState.Empty, events);

    [Fact]
    public void TheUnderlyingStrings_ReallyDoDiffer_SoTheTypeIsLoadBearing()
    {
        // Guards against someone "fixing" this by normalising the constants and
        // concluding the typed id is unnecessary.
        Assert.NotEqual(FirstCase, SecondCase, StringComparer.Ordinal);
        Assert.Equal(FirstCase, SecondCase, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rediscovery_InDifferentCase_MergesTheSameBoard_RatherThanAddingASecond()
    {
        // The reset shape: the board is discovered, disappears, and comes back with
        // its instance id in a different case. It is one physical board, so the
        // reducer must fold it onto the existing row.
        var s = With(
            new AppEvent.BoardDiscovered(Id(FirstCase, "CDYHINBH", version: 1)),
            new AppEvent.BoardDiscovered(Id(SecondCase, "CDYHINBH", version: 2)));

        var board = Assert.Single(s.Boards);
        Assert.Equal(2, board.Version); // merged, not a stale first row
    }

    [Fact]
    public void Removal_InDifferentCase_RemovesTheBoard()
    {
        // The disappear notification and the enumeration snapshot need not agree on
        // case. A case-sensitive RemoveAll leaves a phantom board on screen forever.
        var s = With(
            new AppEvent.BoardDiscovered(Id(FirstCase)),
            new AppEvent.BoardRemoved(SecondCase));

        Assert.Empty(s.Boards);
        Assert.Null(s.SelectedBoardId);
    }

    [Fact]
    public void Lookup_InDifferentCase_FindsTheBoard()
    {
        // Every intent the UI raises carries an id back in; if Find misses, the
        // operation is silently dropped against a board that is right there.
        var s = With(new AppEvent.BoardDiscovered(Id(FirstCase, "CDYHINBH")));

        var found = s.Find(SecondCase);

        Assert.NotNull(found);
        Assert.Equal("CDYHINBH", found!.Serial);
    }

    [Fact]
    public void Selection_InDifferentCase_ResolvesToTheDiscoveredBoard()
    {
        // Select() ignores unknown ids by design, so a case miss silently refuses
        // the selection instead of focusing the board.
        // The other board is discovered first so it wins the auto-selection; the
        // assertion below therefore proves the SelectionChanged actually landed
        // rather than passing on the auto-select that was already there.
        var s = With(
            new AppEvent.BoardDiscovered(Id(@"USB\VID_10C4&PID_8A7E\IMNUZ6YW", "IMNUZ6YW")),
            new AppEvent.BoardDiscovered(Id(FirstCase, "CDYHINBH")),
            new AppEvent.SelectionChanged(SecondCase));

        Assert.Equal(FirstCase, s.SelectedBoardId?.ToString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("CDYHINBH", s.Selected?.Serial);
    }

    [Fact]
    public void PerBoardUpdate_InDifferentCase_ReachesTheBoard()
    {
        // WithBoard is the fold used by every per-board event (pin mode, firmware
        // progress, I2C, errors). A case miss makes each one a silent no-op.
        var s = With(
            new AppEvent.BoardDiscovered(Id(FirstCase)),
            new AppEvent.PinModeChanged(SecondCase, 3, PinMode.PushPullOutput));

        var board = Assert.Single(s.Boards);
        Assert.Equal(PinMode.PushPullOutput, board.Pins[3].Mode);
    }
}
