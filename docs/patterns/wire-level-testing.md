# Wire-level testing for Periphery extensions

> **When to read this:** you want to verify that what your code told
> a hardware device to do **actually reaches the wires** — not just
> "the firmware accepted the command, presumably without errors." The
> tier above lifecycle testing
> ([`usb-lifecycle-testing.md`](usb-lifecycle-testing.md)) for
> hardware-driver-shaped extensions: probe the data lines with a
> logic analyzer, decode the bus traffic, and assert against it from
> the test code.
>
> **Status:** stretch goal. Framework not yet implemented. Doc
> captures the design so we can land it cleanly when needed.

The goal is **out-of-band verification of physical signals**. The
previous testing tiers stop at the firmware boundary — we trust the
device acted on our request because it didn't return an error. Wire-
level testing closes the loop: we observe what the device actually
drove on its pins and assert it matches our intent.

This is the only tier that catches:

- **Protocol-encoding bugs** in our wire-protocol packets where the
  byte happens to be "close enough" that the firmware doesn't error.
- **Configuration miscoding**: I²C set to 400 kHz in our code but
  the firmware actually clocks it at 100 kHz because we put the rate
  in the wrong byte slot.
- **Pin-reservation bugs**: configured the wrong pin's mode and
  didn't notice because nothing read the pin we *did* configure.
- **Mode-bit bugs in SPI**: CPOL/CPHA inverted, byte order wrong,
  chip-select polarity flipped.
- **Off-by-one in clock dividers**, off-by-one in bit timing, etc.

It does **not** catch analog-domain issues (rise time, ringing,
crosstalk). For those you'd want an oscilloscope, not a logic
analyzer — but those aren't typically *our* bug class. Our bugs live
in software-driving-firmware-driving-pins; this tier covers the
whole chain.

---

## Where this fits

| Tier | What it verifies | Hardware required | Typical run frequency |
|---|---|---|---|
| Unit (`FakeUsbBackend`) | C# logic, protocol encoding | none | every PR |
| Integration ([`ILifecycleHarness`](usb-lifecycle-testing.md)) | OS interactions, reconnect | Treehopper | every PR (Linux runner) |
| **Wire-level (`IBusVerifier`)** | **wire-correctness, full stack including firmware** | **Treehopper + logic analyzer** | **release gate / nightly / on-demand** |
| Cross-OS (usbip / VM passthrough) | platform backends | Treehopper + Windows runner | nightly + pre-release |
| Pre-release physical (Pi Zero gadget) | cable / EMI / timing | Pi Zero gadget | pre-release manual |

Wire-level tests are deliberately **not on the per-PR critical path**.
They're slower (captures take seconds, not milliseconds), require a
physically-wired test rig, and the per-test value is concentrated —
you don't need a hundred of them, you need ten well-chosen ones.

---

## The mechanism: Saleae Logic 2 automation

Saleae's [Logic 2 automation API][saleae-auto] is a gRPC interface
exposed by the Logic 2 desktop app. Tests connect to a local gRPC
endpoint, drive captures, configure protocol analyzers, and read
back decoded frame data.

[saleae-auto]: https://github.com/saleae/logic2-automation

The shape:

```bash
# Start Logic 2 with the automation server enabled
logic2 --automation
```

The app listens on `localhost:10430` for gRPC. The official Saleae
team publishes `.proto` files and a Python client; we'd code-gen a
C# client (Grpc.Tools handles this in the build) and wrap it in a
`SaleaeBusVerifier`.

Built-in analyzers cover everything Treehopper's hardware peripherals
expose:

| Treehopper peripheral | Saleae analyzer | Channels needed |
|---|---|---|
| I²C | I2C | 2 (SDA, SCL) |
| SPI | SPI | 4 (MOSI, MISO, SCK, CS) |
| UART | Async Serial | 1–2 (TX, optionally RX) |
| 1-Wire | 1-Wire | 1 |
| GPIO | (none — raw digital) | 1 per pin |
| Soft-PWM | (none — raw digital, measure period) | 1 |
| Hardware PWM | (none — raw digital, measure period) | 1 |
| Parallel | Simple Parallel | 8+ (data + strobe) |

Analyzer output comes back as structured frame data. For I²C, a
frame has `address`, `direction`, `data[]`, `ack`. Test code asserts
against those directly without parsing raw samples.

---

## The `IBusVerifier` abstraction

Tests shouldn't bind to Saleae specifically — same reasoning as
`ILifecycleHarness`. Wrap the analyzer behind an interface and let a
factory pick the implementation at startup based on what hardware
is available.

```csharp
namespace Periphery.Treehopper.IntegrationTests;

/// <summary>
/// Captures and decodes physical bus traffic from a Periphery-driven
/// device under test. Used for wire-level verification of protocol
/// extensions (I²C, SPI, UART, etc.) against an out-of-band oracle.
/// </summary>
public interface IBusVerifier : IAsyncDisposable
{
    /// <summary>
    /// Begins capturing on the specified channels with the given
    /// analyzers. The capture runs until <see cref="IBusCapture"/>
    /// is stopped.
    /// </summary>
    Task<IBusCapture> StartCaptureAsync(BusCaptureConfig config, CancellationToken ct);
}

public sealed record BusCaptureConfig(
    int SampleRateHz,
    IReadOnlyList<int> DigitalChannels,
    IReadOnlyList<BusAnalyzer> Analyzers);

public abstract record BusAnalyzer;
public sealed record I2cAnalyzer(int SdaChannel, int SclChannel) : BusAnalyzer;
public sealed record SpiAnalyzer(
    int MosiChannel, int MisoChannel, int SckChannel, int CsChannel,
    SpiMode Mode = SpiMode.Mode00) : BusAnalyzer;
public sealed record UartAnalyzer(int Channel, int BaudRate, int DataBits = 8) : BusAnalyzer;
// ... 1-Wire, parallel, etc.

public interface IBusCapture : IAsyncDisposable
{
    Task<BusCaptureResult> StopAndDecodeAsync(CancellationToken ct);
}

public sealed record BusCaptureResult(
    TimeSpan Duration,
    IReadOnlyList<DigitalEdge> RawEdges,
    IReadOnlyDictionary<BusAnalyzer, IReadOnlyList<BusFrame>> Frames)
{
    public IEnumerable<T> GetFrames<T>() where T : BusFrame =>
        Frames.SelectMany(kvp => kvp.Value).OfType<T>();
}

public abstract record BusFrame(TimeSpan Timestamp);
public sealed record I2cFrame(
    TimeSpan Timestamp,
    byte Address,
    I2cDirection Direction,
    byte[] Data,
    bool Ack) : BusFrame(Timestamp);
public sealed record SpiFrame(
    TimeSpan Timestamp,
    byte[] Mosi,
    byte[] Miso) : BusFrame(Timestamp);
public sealed record UartFrame(
    TimeSpan Timestamp,
    byte Byte,
    bool ParityOk,
    bool FramingOk) : BusFrame(Timestamp);
```

Concrete implementations:

| Class | Mechanism | Notes |
|---|---|---|
| `SaleaeBusVerifier` | Logic 2 automation gRPC | Default. Requires Logic 2 running on the test host. |
| `SigrokBusVerifier` | `sigrok-cli` shelled out | Fallback for cheaper analyzers (Cypress FX2 clones, DSLogic). Less polished frame output. |

Selector:

```csharp
public static class BusVerifierFactory
{
    public static async Task<IBusVerifier> CreateAsync(CancellationToken ct = default)
    {
        var override_ = Environment.GetEnvironmentVariable("PERIPHERY_BUS_VERIFIER");
        return override_?.ToLowerInvariant() switch
        {
            "saleae" => await SaleaeBusVerifier.ConnectAsync(ct),
            "sigrok" => await SigrokBusVerifier.ConnectAsync(ct),
            null     => await AutoDetectAsync(ct),
            _ => throw new ArgumentException($"Unknown verifier: {override_}"),
        };
    }
}
```

---

## Tests this enables

A small but high-value set. Pick a few per peripheral; resist the
temptation to brute-force-cover every API surface.

### I²C

```csharp
[Fact]
[Trait("Category", "RequiresWireRig")]
public async Task I2c_SendReceive_ProducesCorrectAddressAndData()
{
    using var ct = TestCancellation();
    await using var verifier = await BusVerifierFactory.CreateAsync(ct);
    await using var capture = await verifier.StartCaptureAsync(new BusCaptureConfig(
        SampleRateHz: 25_000_000,
        DigitalChannels: new[] { 0, 1 },                    // CH0=SDA, CH1=SCL
        Analyzers: new[] { (BusAnalyzer)new I2cAnalyzer(SdaChannel: 0, SclChannel: 1) }), ct);

    await using var board = await TreehopperBoard.OpenAsync(deviceInfo, ct);
    await using var i2c = await board.UseI2cAsync(speedKhz: 100, ct);

    await i2c.SendReceiveAsync(address: 0x42, write: new byte[] { 0xAB, 0xCD }, readLen: 0, ct);

    var result = await capture.StopAndDecodeAsync(ct);
    var frames = result.GetFrames<I2cFrame>().ToList();

    Assert.Single(frames);
    Assert.Equal(0x42, frames[0].Address);
    Assert.Equal(I2cDirection.Write, frames[0].Direction);
    Assert.Equal(new byte[] { 0xAB, 0xCD }, frames[0].Data);
    Assert.True(frames[0].Ack);   // assumes a real slave at 0x42 in the rig
}
```

A second test with a register-read pattern (write address byte, then
read N bytes) covers the restart-condition path. Two or three tests
total cover I²C wire-correctness.

### SPI

```csharp
[Fact]
[Trait("Category", "RequiresWireRig")]
public async Task Spi_Mode00_ClocksDataInExpectedOrder()
{
    using var ct = TestCancellation();
    await using var verifier = await BusVerifierFactory.CreateAsync(ct);
    await using var capture = await verifier.StartCaptureAsync(new BusCaptureConfig(
        SampleRateHz: 100_000_000,
        DigitalChannels: new[] { 0, 1, 2, 3 },              // MOSI, MISO, SCK, CS
        Analyzers: new[] { (BusAnalyzer)new SpiAnalyzer(0, 1, 2, 3, SpiMode.Mode00) }), ct);

    await using var board = await TreehopperBoard.OpenAsync(deviceInfo, ct);
    await using var spi = await board.UseSpiAsync(SpiMode.Mode00, speedMhz: 1, ct);
    await spi.SendReceiveAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                               chipSelect: board.Pins[10], ct);

    var result = await capture.StopAndDecodeAsync(ct);
    var frames = result.GetFrames<SpiFrame>().ToList();

    Assert.Single(frames);
    Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, frames[0].Mosi);
}
```

A second test for Mode 11 (CPOL=1, CPHA=1) — different idle-clock
polarity, different latching edge — verifies the mode-bits actually
make it through.

### UART

```csharp
[Fact]
[Trait("Category", "RequiresWireRig")]
public async Task Uart_SendString_ProducesCorrectBaudAndBytes()
{
    using var ct = TestCancellation();
    await using var verifier = await BusVerifierFactory.CreateAsync(ct);
    await using var capture = await verifier.StartCaptureAsync(new BusCaptureConfig(
        SampleRateHz: 10_000_000,
        DigitalChannels: new[] { 0 },                       // TX
        Analyzers: new[] { (BusAnalyzer)new UartAnalyzer(Channel: 0, BaudRate: 115_200) }), ct);

    await using var board = await TreehopperBoard.OpenAsync(deviceInfo, ct);
    await using var uart = await board.UseUartAsync(baud: 115_200, ct);
    await uart.SendAsync(System.Text.Encoding.ASCII.GetBytes("Hello"), ct);

    var result = await capture.StopAndDecodeAsync(ct);
    var bytes = result.GetFrames<UartFrame>()
        .Where(f => f.FramingOk && f.ParityOk)
        .Select(f => f.Byte)
        .ToArray();

    Assert.Equal(System.Text.Encoding.ASCII.GetBytes("Hello"), bytes);
}
```

### GPIO and PWM

Raw digital channels, no analyzer — assert against the captured edge
list directly:

```csharp
[Fact]
[Trait("Category", "RequiresWireRig")]
public async Task SoftPwm_AtFiftyPercent_HasFiftyPercentDutyCycle()
{
    using var ct = TestCancellation();
    await using var verifier = await BusVerifierFactory.CreateAsync(ct);
    await using var capture = await verifier.StartCaptureAsync(new BusCaptureConfig(
        SampleRateHz: 25_000_000,
        DigitalChannels: new[] { 0 },
        Analyzers: Array.Empty<BusAnalyzer>()), ct);

    await using var board = await TreehopperBoard.OpenAsync(deviceInfo, ct);
    await using var pwm = await board.Pins[7].EnableSoftPwmAsync(dutyCycle: 0.5, ct);

    await Task.Delay(TimeSpan.FromMilliseconds(500), ct);    // capture a few cycles

    var result = await capture.StopAndDecodeAsync(ct);

    // Compute duty cycle from raw edges
    var (period, highTime) = MeasurePwm(result.RawEdges);
    Assert.InRange(period.TotalMicroseconds, 16_000, 17_000);   // ~60.94 Hz per spec
    Assert.InRange(highTime.TotalMicroseconds / period.TotalMicroseconds, 0.49, 0.51);
}
```

This is also where you catch firmware bugs you'd otherwise blame on
Periphery: if Treehopper's soft-PWM clock has drifted because the
firmware's timer interrupt is being preempted, the test fails with a
clear "duty cycle wrong" assertion rather than a vague "things seem
off."

---

## Hardware setup

A one-time wiring rig. Document it carefully — future contributors
(and future you) should be able to reproduce it from the doc without
guessing.

### Recommended layout

```
┌────────────────┐                ┌──────────────────────┐
│  Treehopper    │                │  Saleae Logic Pro    │
│                │                │                      │
│  Pin 0 (SCK) ──┼────────────────┼─ CH2  (SPI SCK)      │
│  Pin 1 (MISO)──┼────────────────┼─ CH1  (SPI MISO)     │
│  Pin 2 (MOSI)──┼────────────────┼─ CH0  (SPI MOSI)     │
│  Pin 3 (SDA) ──┼─┬──────────────┼─ CH4  (I²C SDA)      │
│  Pin 4 (SCL) ──┼─┼┬─────────────┼─ CH5  (I²C SCL)      │
│  Pin 5 (TX)  ──┼─┼┼─────────────┼─ CH6  (UART TX)      │
│  Pin 7 (PWM) ──┼─┼┼─────────────┼─ CH7  (PWM / GPIO)   │
│  Pin 10 (CS) ──┼─┼┼─────────────┼─ CH3  (SPI CS)       │
│                │ ││             │                      │
│  GND  ─────────┼─┼┼──────┬──────┼─ GND                 │
└────────────────┘ ││      │      └──────────────────────┘
                   ││      │
                   ↓↓      │
              ┌───────────┐│
              │  I²C      ││
              │  slave    ├┘   (e.g., MCP9808 temp sensor at 0x18,
              │  @0x42    │     or a known dummy responder at 0x42)
              └───────────┘
```

Notes on the rig:

- **Common ground.** Saleae and Treehopper need their grounds tied
  together for the analyzer to see meaningful edges.
- **Pull-ups on the I²C bus.** Treehopper does not provide them.
  4.7kΩ to 3.3V on SDA and SCL.
- **A known-good I²C slave.** Pick something inexpensive and stable
  — MCP9808 (temperature sensor, ~$5), MCP23017 (port expander, ~$2),
  AT24C32 (EEPROM, ~$1). Lets you assert that ACK was received, not
  just that the address went out.
- **Channel assignments are part of the test contract.** Tests
  reference channel numbers; if you re-wire the rig you have to
  update the channel constants in one place. Define them in a
  `WireRigLayout` static class.
- **Photograph the wiring.** Put the photo in `docs/patterns/`. The
  doc that says "CH0 → MOSI" is necessary but not sufficient when
  someone is trying to figure out which probe goes where six
  months later.

### CI / runner setup

The wire-level tier runs on a self-hosted runner with the rig
permanently wired up. Logic 2 must be running with `--automation`;
launch it as a system-startup process so reboots don't break the
runner:

```ini
# /etc/systemd/user/saleae-logic2.service
[Unit]
Description=Saleae Logic 2 (automation mode)
After=graphical-session.target

[Service]
ExecStart=/usr/bin/Logic --automation
Restart=on-failure

[Install]
WantedBy=default.target
```

For CI scheduling, this tier wants to run **on demand** (when an
integration-tagged commit lands) or **nightly**, not per-PR. Tag the
test category and exclude from the default `dotnet test` invocation:

```bash
# Per-PR
dotnet test --filter "Category!=RequiresWireRig&Category!=RequiresRealUsb"

# Nightly / on demand
dotnet test --filter "Category=RequiresWireRig"
```

---

## Alternative analyzer hardware

The `IBusVerifier` interface keeps tests portable across analyzers.
Recommended primary is Saleae Logic 2; the realistic alternatives:

| Hardware | API path | Cost | Sample rate | When it makes sense |
|---|---|---|---|---|
| Saleae Logic Pro 8/16 | Logic 2 gRPC | $500–$800 | 500 MS/s | Best automation story; recommended primary. |
| Saleae Logic 8 | Same | $300 | 100 MS/s | Same API, slower sample rate; fine unless you push SPI to ≥24 MHz. |
| DreamSourceLab DSLogic Plus | sigrok-cli | $150 | 400 MS/s | Sigrok-compatible mid-range. Good fallback. |
| Cypress FX2 clone ("8-channel logic analyzer" on Amazon) | sigrok-cli | $10–$20 | 24 MS/s | Cheap. Insufficient for SPI ≥6 MHz, fine for I²C / UART / GPIO. Acceptable for contributor-side runs. |
| Digilent Analog Discovery 3 | WaveForms SDK | $400 | 125 MS/s | Mixed-signal: also a scope and signal generator. Useful if you also want analog-domain checks. |

**Cost vs. value.** The wire-level tier is by definition not
something every contributor reproduces. The project ships one
runner with one Saleae; contributors who want to run wire-level
tests locally either buy the cheap Cypress clone (and accept SPI
limitations) or skip the tier locally and rely on CI feedback.

---

## What this doesn't cover

Honest scope:

- **Analog domain.** Rise time, ringing, signal integrity. Logic
  analyzers digitize edges; for analog work you need a scope.
- **Cable / EMI quirks.** A logic analyzer probe on a clean test
  rig sees a different signal than a 10-foot USB cable run through
  a noisy office. The pre-release Pi Zero gadget tier is where you'd
  catch those.
- **Firmware bugs that look correct on the wire.** If the firmware
  acks an I²C transaction and clocks data correctly but stores the
  wrong byte internally, this tier doesn't see it. Other peripherals
  on the bus would.
- **Bus contention with other masters.** Treehopper is master-only;
  a multi-master scenario would need a different rig.

---

## Implementation order (when we land this)

Once Periphery.Treehopper v1 is shipping:

1. **Pull the Saleae `.proto` files** and generate a C# gRPC client
   in a new project, `tools/Periphery.Saleae.Automation/`. Do this
   first; it's the largest unknown.
2. **`SaleaeBusVerifier`** wrapping the gRPC client behind the
   `IBusVerifier` shape above. ~500 lines.
3. **Wire the rig**, photograph it, document it.
4. **`tests/Periphery.Treehopper.WireTests/`** — separate project
   (so it can have a different runner profile in CI). ~10 tests
   total: 2 I²C, 2 SPI, 1 UART, 1 PWM, 1 GPIO, 1 1-Wire, 1
   parallel.
5. **CI workflow** that runs the wire tests on the self-hosted
   runner nightly + on-demand via workflow_dispatch.
6. **`SigrokBusVerifier`** as a follow-up if/when a contributor
   wants to run wire tests on cheaper hardware.

Estimate: ~2 weeks once Periphery.Treehopper v1 is in place. The
gRPC code-gen and Saleae automation client is the bulk of the work;
the actual tests are short.

---

## References

- [Saleae Logic 2 automation API][saleae-auto] (gRPC, .proto files,
  Python reference client)
- [sigrok-cli documentation](https://sigrok.org/wiki/Sigrok-cli)
- [DreamSourceLab DSLogic](https://www.dreamsourcelab.com/) (sigrok-
  compatible mid-range analyzer)
- [`docs/patterns/usb-lifecycle-testing.md`](usb-lifecycle-testing.md)
  — the lifecycle tier this builds on
- [ADR-0039 — Periphery.Treehopper](../adr/0039-periphery-treehopper.md)
- [Plan: Periphery.Usb + Periphery.Treehopper](../plans/periphery-treehopper.md)
