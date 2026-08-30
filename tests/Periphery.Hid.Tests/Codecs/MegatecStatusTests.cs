namespace Periphery.Hid.Tests.Codecs;

/// <summary>
/// Tests for the dialect-agnostic pure parser <see cref="MegatecStatus"/>.
/// Inputs are drawn from live wire traces against real hardware (the ADR-0048
/// spike's WayTech, and a second unit's QS dialect)
/// plus synthetic edge cases. The status-line shape is identical across dialects
/// — only the verb that elicits it differs — so one parser covers Q1, QS, etc.
/// </summary>
public class MegatecStatusTests
{
    // Q1-dialect trace from the spike (line power present, single 12V battery
    // near float, no temperature sensor). Prefix-inclusive, terminator-
    // exclusive — exactly the shape MegatecWire returns.
    private const string SampleLinePower =
        "(119.6 119.6 119.6 008 60.1 13.7 --.- 00001001";

    // Live QS-dialect capture from a deployed UPS,
    // periphery as sole consumer: `QS\r` → this line. The whole point of the
    // claim-and-bind fix is that this unit speaks QS, not Q1 — but the *parser*
    // must decode it identically because the response shape is the same.
    // Decodes to: input 117.4V, output 117.4V, 0% load, 60.2Hz, battery 13.7V
    // (~100% on a 12V cell), no temp sensor, status 00001001 → on line power
    // (b7=0), not low (b6=0), standby topology (b3=1), beeper on (b0=1).
    private const string VoltronicQsCapture =
        "(117.4 117.4 117.4 000 60.2 13.7 --.- 00001001";

    [Fact]
    public void Parse_QsCapture_IsExternalPowerConnected()
    {
        var snapshot = MegatecStatus.Parse(VoltronicQsCapture);
        Assert.True(snapshot.IsExternalPowerConnected);
    }

    [Fact]
    public void Parse_QsCapture_NotCharging()
    {
        var snapshot = MegatecStatus.Parse(VoltronicQsCapture);
        Assert.Equal(BatteryStatus.NotCharging, snapshot.BatteryStatus);
    }

    [Fact]
    public void Parse_QsCapture_ChargeIsFullForFloatVoltage()
    {
        // 13.7V on the 10.5–13.6V single-cell curve clamps to 100%.
        var snapshot = MegatecStatus.Parse(VoltronicQsCapture);
        Assert.Equal(100, snapshot.BatteryChargePercent);
    }

    [Fact]
    public void Parse_QsCapture_NotLow()
    {
        var snapshot = MegatecStatus.Parse(VoltronicQsCapture);
        Assert.False(snapshot.IsBatteryLow);
    }

    [Fact]
    public void Parse_LinePowerSample_IsExternalPowerConnected()
    {
        var snapshot = MegatecStatus.Parse(SampleLinePower);
        Assert.True(snapshot.IsExternalPowerConnected);
    }

    [Fact]
    public void Parse_LinePowerSample_NotCharging()
    {
        // Utility-fail bit clear → on line power. The Megatec dialects don't
        // expose a distinct "charging vs float" signal, so the codec reports
        // NotCharging when AC is present (charge state can't be inferred).
        var snapshot = MegatecStatus.Parse(SampleLinePower);
        Assert.Equal(BatteryStatus.NotCharging, snapshot.BatteryStatus);
    }

    [Fact]
    public void Parse_LinePowerSample_ChargeIsFullForFloatVoltage()
    {
        var snapshot = MegatecStatus.Parse(SampleLinePower);
        Assert.Equal(100, snapshot.BatteryChargePercent);
    }

    [Theory]
    // Synthetic responses covering the two orthogonal status axes
    // (b7 = utility fail, b6 = battery low). Discharging vs NotCharging
    // tracks utility-fail; IsBatteryLow surfaces bit 6 independently so
    // a charging-from-empty UPS or a discharging-and-low UPS report both
    // facts without one collapsing the other.
    [InlineData("00000000", true,  false, false)]  // line power, healthy
    [InlineData("10000000", false, true,  false)]  // on battery, not low
    [InlineData("11000000", false, true,  true )]  // on battery, low (imminent shutdown)
    [InlineData("01000000", true,  false, true )]  // line power but battery still low (recovering from depletion)
    [InlineData("00001001", true,  false, false)]  // line power, standby UPS, beeper on
    public void Parse_StatusBits_PopulatesExternalAndStatus(
        string statusBits, bool expectExternal, bool expectOnBattery, bool expectBatteryLow)
    {
        var response = $"(120.0 120.0 120.0 010 60.0 13.0 25.0 {statusBits}";
        var snapshot = MegatecStatus.Parse(response);

        Assert.Equal(expectExternal, snapshot.IsExternalPowerConnected);
        Assert.Equal(
            expectOnBattery ? BatteryStatus.Discharging : BatteryStatus.NotCharging,
            snapshot.BatteryStatus);
        Assert.Equal(expectBatteryLow, snapshot.IsBatteryLow);
    }

    [Theory]
    [InlineData("10.5", 0)]     // empty
    [InlineData("12.0", 48)]    // mid-discharge
    [InlineData("13.6", 100)]   // full
    [InlineData("13.7", 100)]   // float (clamped)
    [InlineData("13.0", 81)]    // typical resting (rounded from 80.6)
    public void Parse_ChargePercentEstimation_SingleCellCurve(
        string voltageField, int expectedPercent)
    {
        var response = $"(120.0 120.0 120.0 005 60.0 {voltageField} --.- 00000000";
        var snapshot = MegatecStatus.Parse(response);
        Assert.Equal(expectedPercent, snapshot.BatteryChargePercent);
    }

    [Theory]
    [InlineData("24.0")]  // multi-cell (24V pack) — out of single-cell range
    [InlineData("48.0")]  // multi-cell (48V pack)
    [InlineData("08.0")]  // pathologically low
    public void Parse_ChargePercent_OutOfSingleCellRange_ReturnsNull(string voltageField)
    {
        var response = $"(120.0 120.0 120.0 005 60.0 {voltageField} --.- 00000000";
        var snapshot = MegatecStatus.Parse(response);
        Assert.Null(snapshot.BatteryChargePercent);
    }

    [Fact]
    public void Parse_TemperatureFieldMissing_StillParses()
    {
        // "--.-" temperature is normal for UPSs without a sensor; the parser
        // ignores temperature anyway. Verifies it doesn't try to interpret it
        // as a number.
        var response = "(120.0 120.0 120.0 005 60.0 13.0 --.- 00000000";
        var snapshot = MegatecStatus.Parse(response);
        Assert.NotNull(snapshot.BatteryChargePercent);
    }

    [Fact]
    public void Parse_MissingPrefix_Throws()
    {
        Assert.Throws<HidTransferException>(() =>
            MegatecStatus.Parse("120.0 120.0 120.0 005 60.0 13.0 --.- 00000000"));
    }

    [Fact]
    public void Parse_TooFewFields_Throws()
    {
        Assert.Throws<HidTransferException>(() =>
            MegatecStatus.Parse("(120.0 120.0 120.0"));
    }

    [Fact]
    public void Parse_ShortStatusBits_Throws()
    {
        Assert.Throws<HidTransferException>(() =>
            MegatecStatus.Parse("(120.0 120.0 120.0 005 60.0 13.0 25.0 0001"));
    }

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => MegatecStatus.Parse(null!));
    }

    // ── IsWellFormed: the non-throwing predicate claim-and-bind detection
    //    uses to decide whether a probe actually answered. Must agree with
    //    Parse (true ⟺ Parse would succeed). ────────────────────────────────

    [Theory]
    [InlineData("(119.6 119.6 119.6 008 60.1 13.7 --.- 00001001")]  // Q1 sample
    [InlineData("(117.4 117.4 117.4 000 60.2 13.7 --.- 00001001")]  // QS capture
    [InlineData("(120.0 120.0 120.0 010 60.0 13.0 25.0 10000000")]  // on battery
    public void IsWellFormed_ValidStatusLines_True(string response)
    {
        Assert.True(MegatecStatus.IsWellFormed(response));
    }

    [Theory]
    [InlineData(null)]                                              // no response at all
    [InlineData("")]                                               // empty
    [InlineData("120.0 120.0 120.0 005 60.0 13.0 --.- 00000000")]  // missing '(' prefix
    [InlineData("(120.0 120.0 120.0")]                             // too few fields
    [InlineData("(120.0 120.0 120.0 005 60.0 13.0 25.0 0001")]     // status bits too short
    public void IsWellFormed_Malformed_False(string? response)
    {
        Assert.False(MegatecStatus.IsWellFormed(response));
    }
}
