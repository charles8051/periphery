using System.Collections.Immutable;
using System.Linq;
using Periphery.Treehopper;
using Periphery.Treehopper.Control;
using Xunit;

namespace Periphery.Treehopper.Control.Tests;

public class AppReducerTests
{
    private static BoardIdentity Id(string id, string? serial = null, int? version = null,
        BoardConnection conn = BoardConnection.Application)
        => new(id, serial, Name: null, Version: version, Connection: conn);

    private static AppState With(params AppEvent[] events) => AppReducer.ReduceAll(AppState.Empty, events);

    // ── Discovery ────────────────────────────────────────────────────────

    [Fact]
    public void BoardDiscovered_AddsBoard_With20ReservedPins_AndAutoSelectsFirst()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A", "SN-A")));

        var b = Assert.Single(s.Boards);
        Assert.Equal("A", b.Id);
        Assert.Equal("SN-A", b.Serial);
        Assert.Equal(BoardView.PinCount, b.Pins.Length);
        Assert.All(b.Pins, p => Assert.Equal(PinMode.Reserved, p.Mode));
        Assert.Equal(Enumerable.Range(0, 20), b.Pins.Select(p => p.Number));
        Assert.Equal("A", s.SelectedBoardId); // auto-selected
    }

    [Fact]
    public void BoardDiscovered_SecondBoard_DoesNotChangeSelection()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.BoardDiscovered(Id("B")));

        Assert.Equal(2, s.Boards.Length);
        Assert.Equal("A", s.SelectedBoardId);
    }

    [Fact]
    public void BoardDiscovered_Existing_MergesIdentity_PreservesPins()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A", "SN-A")),
            new AppEvent.PinModeChanged("A", 3, PinMode.PushPullOutput),
            new AppEvent.BoardDiscovered(Id("A", serial: null, version: 274))); // re-discovery, no serial

        var b = s.Find("A")!;
        Assert.Equal("SN-A", b.Serial);              // preserved (new identity had null serial)
        Assert.Equal(274, b.Version);                // merged in
        Assert.Equal(PinMode.PushPullOutput, b.Pins[3].Mode); // pin state preserved
    }

    // ── Removal ──────────────────────────────────────────────────────────

    [Fact]
    public void BoardRemoved_DropsBoard_AndReselectsFirstRemaining()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.BoardDiscovered(Id("B")),
            new AppEvent.BoardRemoved("A")); // A was selected

        Assert.Equal("B", Assert.Single(s.Boards).Id);
        Assert.Equal("B", s.SelectedBoardId);
    }

    [Fact]
    public void BoardRemoved_LastBoard_ClearsSelection()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")), new AppEvent.BoardRemoved("A"));
        Assert.Empty(s.Boards);
        Assert.Null(s.SelectedBoardId);
    }

    [Fact]
    public void BoardRemoved_NonSelected_KeepsSelection()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.BoardDiscovered(Id("B")),
            new AppEvent.BoardRemoved("B"));
        Assert.Equal("A", s.SelectedBoardId);
    }

    // ── Selection ────────────────────────────────────────────────────────

    [Fact]
    public void SelectionChanged_SetsKnown_IgnoresUnknown_ClearsOnNull()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")), new AppEvent.BoardDiscovered(Id("B")));

        Assert.Equal("B", AppReducer.Reduce(s, new AppEvent.SelectionChanged("B")).SelectedBoardId);
        Assert.Equal("A", AppReducer.Reduce(s, new AppEvent.SelectionChanged("ghost")).SelectedBoardId); // unchanged
        Assert.Null(AppReducer.Reduce(s, new AppEvent.SelectionChanged(null)).SelectedBoardId);
    }

    // ── Pin mode ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public void PinModeChanged_SetsThatPinOnly(int pin)
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.PinModeChanged("A", pin, PinMode.PushPullOutput));

        var b = s.Find("A")!;
        Assert.Equal(PinMode.PushPullOutput, b.Pins[pin].Mode);
        Assert.Equal(19, b.Pins.Count(p => p.Mode == PinMode.Reserved)); // the other 19 unchanged
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    public void PinModeChanged_OutOfRange_IsNoOp(int pin)
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.PinModeChanged("A", pin, PinMode.PushPullOutput));
        Assert.All(s.Find("A")!.Pins, p => Assert.Equal(PinMode.Reserved, p.Mode));
    }

    // ── Output drive (host-authoritative level) ──────────────────────────

    [Fact]
    public void OutputDriven_SetsPushPullModeAndLevel()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.OutputDriven("A", 13, High: false));
        var pin = s.Find("A")!.Pins[13];
        Assert.Equal(PinMode.PushPullOutput, pin.Mode);
        Assert.False(pin.High);
    }

    [Fact]
    public void ReportReceived_DoesNotClobberDrivenOutputLevel()
    {
        // Drive pin 13 low, then a report (which reads the pin high) must not flip it.
        var s = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.OutputDriven("A", 13, High: false),
            new AppEvent.ReportReceived("A", Report((13, true, 0), (7, true, 0))));

        var b = s.Find("A")!;
        Assert.False(b.Pins[13].High);  // output level preserved (host-authoritative)
        Assert.True(b.Pins[7].High);    // input reflects the report
    }

    // ── Reports ──────────────────────────────────────────────────────────

    private static BoardReport Report(params (int pin, bool high, int adc)[] sets)
    {
        var pins = Enumerable.Range(0, 20).Select(_ => new PinSnapshot(false, 0)).ToArray();
        foreach (var (pin, high, adc) in sets) pins[pin] = new PinSnapshot(high, adc);
        return new BoardReport(1, pins.ToImmutableArray());
    }

    [Fact]
    public void ReportReceived_MapsDigitalAndAdcPerPin()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.ReportReceived("A", Report((3, true, 0), (5, false, 2000))));

        var b = s.Find("A")!;
        Assert.True(b.Pins[3].High);
        Assert.Equal(2000, b.Pins[5].Adc);
        Assert.False(b.Pins[0].High);
    }

    [Fact]
    public void ReportReceived_ShorterReport_UpdatesPrefix_NoThrow()
    {
        var shortReport = new BoardReport(1, ImmutableArray.Create(
            new PinSnapshot(true, 0), new PinSnapshot(true, 0)));
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.ReportReceived("A", shortReport));

        var b = s.Find("A")!;
        Assert.True(b.Pins[0].High);
        Assert.True(b.Pins[1].High);
        Assert.Equal(20, b.Pins.Length);
    }

    // ── Firmware gating ──────────────────────────────────────────────────

    [Fact]
    public void FirmwareTarget_AndVersion_DeriveStatus()
    {
        // target then version
        var below = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.FirmwareTargetSet(274),
            new AppEvent.BoardVersionRead("A", 273));
        Assert.Equal(FirmwareStatus.UpdateAvailable, below.Find("A")!.Firmware.Status);

        var atTarget = AppReducer.Reduce(below, new AppEvent.BoardVersionRead("A", 274));
        Assert.Equal(FirmwareStatus.UpToDate, atTarget.Find("A")!.Firmware.Status);
    }

    [Fact]
    public void FirmwareTargetSet_RecomputesAllBoards()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A", version: 273)),
            new AppEvent.BoardDiscovered(Id("B", version: 300)),
            new AppEvent.BoardVersionRead("A", 273),
            new AppEvent.BoardVersionRead("B", 300),
            new AppEvent.FirmwareTargetSet(274));

        Assert.Equal(FirmwareStatus.UpdateAvailable, s.Find("A")!.Firmware.Status);
        Assert.Equal(FirmwareStatus.UpToDate, s.Find("B")!.Firmware.Status);
    }

    [Fact]
    public void Firmware_UnknownWhenNoTargetOrNoVersion()
    {
        var noTarget = With(new AppEvent.BoardDiscovered(Id("A", version: 273)),
            new AppEvent.BoardVersionRead("A", 273));
        Assert.Equal(FirmwareStatus.Unknown, noTarget.Find("A")!.Firmware.Status);

        var noVersion = With(new AppEvent.BoardDiscovered(Id("A")), new AppEvent.FirmwareTargetSet(274));
        Assert.Equal(FirmwareStatus.Unknown, noVersion.Find("A")!.Firmware.Status);
    }

    // ── Firmware lifecycle ───────────────────────────────────────────────

    [Fact]
    public void Firmware_Started_Progressed_FinishedSuccess()
    {
        var started = With(
            new AppEvent.BoardDiscovered(Id("A", version: 273)),
            new AppEvent.FirmwareTargetSet(274),
            new AppEvent.FirmwareUpdateStarted("A"));
        Assert.Equal(FirmwareStatus.Updating, started.Find("A")!.Firmware.Status);
        Assert.Equal(0, started.Find("A")!.Firmware.Percent);

        var mid = AppReducer.Reduce(started, new AppEvent.FirmwareProgressed("A", 5, 10));
        Assert.Equal(50, mid.Find("A")!.Firmware.Percent);

        var done = AppReducer.Reduce(mid, new AppEvent.FirmwareUpdateFinished("A", Success: true, NewVersion: 274));
        var b = done.Find("A")!;
        Assert.Equal(FirmwareStatus.Updated, b.Firmware.Status);
        Assert.Equal(274, b.Version);
        Assert.Null(b.LastError);
    }

    [Fact]
    public void Firmware_FinishedFailure_SetsFailedAndLastError()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.FirmwareUpdateStarted("A"),
            new AppEvent.FirmwareUpdateFinished("A", Success: false, Message: "CRC error at record 7"));

        var b = s.Find("A")!;
        Assert.Equal(FirmwareStatus.Failed, b.Firmware.Status);
        Assert.Equal("CRC error at record 7", b.Firmware.Message);
        Assert.Equal("CRC error at record 7", b.LastError);
    }

    [Fact]
    public void FirmwareProgress_ZeroTotal_IsHundredPercent()
        => Assert.Equal(100, AppReducer.Reduce(
            With(new AppEvent.BoardDiscovered(Id("A")), new AppEvent.FirmwareUpdateStarted("A")),
            new AppEvent.FirmwareProgressed("A", 0, 0)).Find("A")!.Firmware.Percent);

    [Fact]
    public void FirmwareTargetSet_WhileUpdating_DoesNotClobberProgress()
    {
        var s = With(
            new AppEvent.BoardDiscovered(Id("A", version: 273)),
            new AppEvent.FirmwareUpdateStarted("A"),
            new AppEvent.FirmwareProgressed("A", 5, 10),
            new AppEvent.FirmwareTargetSet(274)); // must not reset the in-flight flash

        var b = s.Find("A")!;
        Assert.Equal(FirmwareStatus.Updating, b.Firmware.Status);
        Assert.Equal(50, b.Firmware.Percent);
    }

    // ── I2C scan ─────────────────────────────────────────────────────────

    [Fact]
    public void I2cScan_StartedThenFinished_RecordsResponders()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")), new AppEvent.I2cScanStarted("A"));
        Assert.True(s.Find("A")!.I2cScanning);

        var done = AppReducer.Reduce(s,
            new AppEvent.I2cScanFinished("A", ImmutableArray.Create<byte>(0x48, 0x68)));
        var b = done.Find("A")!;
        Assert.False(b.I2cScanning);
        Assert.Equal(new byte[] { 0x48, 0x68 }, b.I2cResponders!.Value);
    }

    // ── Errors ───────────────────────────────────────────────────────────

    [Fact]
    public void OperationFailed_SetsLastError()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")),
            new AppEvent.OperationFailed("A", "board went away"));
        Assert.Equal("board went away", s.Find("A")!.LastError);
    }

    // ── Events targeting an unknown board are no-ops (don't throw) ────────

    [Fact]
    public void EventsForUnknownBoard_AreNoOps()
    {
        var s = With(new AppEvent.BoardDiscovered(Id("A")));
        var same = AppReducer.ReduceAll(s,
            new AppEvent.BoardVersionRead("ghost", 1),
            new AppEvent.PinModeChanged("ghost", 0, PinMode.PushPullOutput),
            new AppEvent.I2cScanStarted("ghost"),
            new AppEvent.OperationFailed("ghost", "x"));
        Assert.Equal(s, same); // record equality: state untouched
    }
}
