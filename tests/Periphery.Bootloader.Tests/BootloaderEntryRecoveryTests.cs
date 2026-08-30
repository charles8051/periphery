using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Recovery for a failed mode switch (ADR-0076): when a device will not enter its bootloader,
/// the orchestrator drives the ADR-0060 seam — reset, wait for the device to come back, retry —
/// rather than ending the run. All driven by fakes; no hardware.
/// </summary>
/// <remarks>
/// The failure being recovered from is the one that makes a wedged board unflashable: the mode
/// switch rides the device's normal data path, so a stuck data path means the updater cannot tell
/// the device to enter its bootloader at all.
/// </remarks>
public class BootloaderEntryRecoveryTests
{
    private const string PortA = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(3)";

    private static DeviceInfo Dev(string id, ushort vid, ushort pid, string? location = PortA) =>
        new() { Id = id, VendorId = new HardwareId(vid), ProductId = new HardwareId(pid), LocationPath = location };

    // A serial as well as a port: identity now requires BOTH, so a device with only one of them
    // is deliberately not re-acquirable (see IdentityFilterFor).
    private static readonly DeviceInfo App = Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" };
    private static readonly DeviceInfo Boot = Dev("boot", 0x10C4, 0xEAC9);
    private static readonly DeviceFilter BootFilter = new DeviceFilter().WithUsbId("10C4", "EAC9");

    private static readonly ResetStrategy Soft =
        new(ResetKind.SoftProtocol, ResetBlastRadius.Self, ReEnumerates: true);
    private static readonly ResetStrategy OutOfBand =
        new(ResetKind.SoftProtocolOutOfBand, ResetBlastRadius.Self, ReEnumerates: true);

    /// <summary>
    /// A STATEFUL fake, deliberately: it tracks which devices are currently present and replays that
    /// set from <see cref="StartAsync"/>, exactly as a real watcher does.
    /// </summary>
    /// <remarks>
    /// The earlier version replayed only a fixed constructor list and dropped anything fired while
    /// nobody was subscribed. That is not how discovery behaves, and the difference was not cosmetic
    /// — it hid a real defect (a "wait" that never waited, because the device was already present at
    /// arm) and then hid its replacement's failure too. A fake that cannot be wrong in the same ways
    /// as the real thing is not testing the real thing.
    /// </remarks>
    private sealed class FakeWaitSource(IEnumerable<DeviceInfo>? present = null) : IDeviceWaitSource
    {
        private readonly List<DeviceInfo> _present = present?.ToList() ?? [];

        /// <summary>
        /// The filter this source was created for. Applied to everything it emits, exactly as
        /// <c>DeviceWatcherWaitSource</c> does via <c>Devices.Watch().Where(filter.Matches)</c>.
        /// Without this the fake emits devices the real source would never surface — and since the
        /// safety argument for recovery is precisely "identity lives in the filter", a fake that
        /// ignores filters does not test identity at all. It let a sibling be adopted in a test
        /// whose entire purpose was to prove a sibling cannot be.
        /// </summary>
        public DeviceFilter? Filter { get; set; }

        private bool Admits(DeviceInfo d) => Filter is null || Filter.Matches(d);

        public event Action<DeviceInfo>? Appeared;
        public event Action<string>? Disappeared;

        public Task StartAsync(CancellationToken ct)
        {
            foreach (var d in _present.ToList()) if (Admits(d)) Appeared?.Invoke(d);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>The device arrives and STAYS present, so a later wait sees it in the snapshot.</summary>
        public void Fire(DeviceInfo d)
        {
            _present.RemoveAll(x => string.Equals(x.Id.ToString(), d.Id.ToString(), StringComparison.OrdinalIgnoreCase));
            _present.Add(d);
            if (Admits(d)) Appeared?.Invoke(d);
        }

        /// <summary>The device leaves the bus and stays gone until it is fired again.</summary>
        public void Drop(string id)
        {
            _present.RemoveAll(x => string.Equals(x.Id.ToString(), id, StringComparison.OrdinalIgnoreCase));
            Disappeared?.Invoke(id);
        }
    }

    private sealed class FakeEntry(DeviceFilter expected, Func<DeviceInfo, Task> onEnter) : IBootloaderEntry
    {
        public string Name => "Fake";
        public bool CanEnter(DeviceInfo d) => true;
        public DeviceFilter ExpectedBootloader => expected;
        public Task EnterAsync(DeviceInfo d, CancellationToken ct) => onEnter(d);
    }

    /// <summary>A reset that records what was asked of it and runs a per-call script.</summary>
    private sealed class FakeReset(
        IReadOnlyList<ResetStrategy> strategies,
        Func<DeviceInfo, ResetStrategy, Task<ResetOutcome>>? onReset = null) : IDeviceReset
    {
        public List<ResetStrategy> Performed { get; } = [];
        public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device) => strategies;
        public async ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
        {
            Performed.Add(strategy);
            return onReset is null ? ResetOutcome.Issued : await onReset(device, strategy);
        }
    }

    private sealed class GateSaying(bool safe) : IResetSafetyGate
    {
        public ValueTask<bool> CanResetAsync(DeviceInfo device, CancellationToken ct) => new(safe);
    }

    // Route the bootloader wait and the application-return wait to their own fake sources, the way
    // real discovery would: each source is created for, and filtered by, one filter.
    private static Func<DeviceFilter, IDeviceWaitSource> Route(FakeWaitSource boot, FakeWaitSource app)
        => f =>
        {
            // Hand the source the filter it was created for, so it emits only what the real
            // watcher would. Recovery's identity filter is how a sibling is excluded at all.
            var target = f.Matches(Boot) ? boot : app;
            target.Filter = f;
            return target;
        };

    // `correlation` is the BOOTLOADER correlation (which board's bootloader is ours). It is a
    // separate axis from how recovery re-acquires the APPLICATION device after a reset — the tests
    // below that exercise the latter deliberately use a device with no LocationPath, which
    // ByLocationPath rejects outright for the former.
    private static BootloaderEntryOptions WithRecovery(
        IDeviceReset reset, IResetSafetyGate? gate = null, IRecoveryPolicy? policy = null,
        DeviceCorrelationMode correlation = DeviceCorrelationMode.ByLocationPath)
        => new()
        {
            Correlation = correlation,
            // Keep the failing waits short: several tests deliberately let the bootloader wait time out.
            BootloaderTimeout = TimeSpan.FromMilliseconds(150),
            Recovery = new BootloaderEntryRecovery(
                reset, Policy: policy, SafetyGate: gate, ReturnTimeout: TimeSpan.FromSeconds(2)),
        };

    // ── The headline case ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wedged_board_is_reset_and_retried_instead_of_failing_the_update()
    {
        var bootSource = new FakeWaitSource();
        // The board is PRESENT when the return wait arms — it has not been reset yet. Modelling that
        // matters: an earlier revision used an empty source here, and that unrealistic fake hid a
        // real defect (identity correlation matches an already-present device immediately, so the
        // "wait" returned the pre-reset snapshot without ever waiting).
        var appSource = new FakeWaitSource([App]);

        // The board comes back under a DIFFERENT-CASED id (periphery #231) — proving recovery
        // re-acquires the snapshot rather than reusing the stale one it was handed.
        var appAfterReset = Dev("APP", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" };

        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");                      // it leaves the bus...
            appSource.Fire(appAfterReset);              // ...and re-enumerates
            return Task.FromResult(ResetOutcome.Issued);
        });

        // EnterAsync throws while wedged (the open reconciles over the stuck endpoint), and
        // succeeds once a reset has happened.
        int entered = 0;
        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            entered++;
            seenByEnter.Add(d.Id!);
            if (reset.Performed.Count == 0)
                throw new TimeoutException("peripheral-config endpoint did not drain");
            bootSource.Fire(Boot);
            return Task.CompletedTask;
        });

        DeviceInfo? flashed = null;
        var phases = new List<BootloaderEntryPhase>();

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashed = dev; return Task.FromResult("ok"); },
            options: WithRecovery(reset),
            phase: new Progress(phases.Add),
            waitSource: Route(bootSource, appSource));

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal("boot", flashed!.Id);

        // One sanity retry, then the gentlest rung — not a jump straight to the hard ones.
        Assert.Equal(3, entered);
        Assert.Equal([ResetKind.SoftProtocol], reset.Performed.Select(s => s.Kind));

        // The retried entry was handed the REFRESHED device, not the pre-reset snapshot.
        Assert.Equal(["app", "app", "APP"], seenByEnter);
        Assert.Contains(BootloaderEntryPhase.Recovering, phases);
    }

    [Fact]
    public async Task Escalates_to_the_out_of_band_rung_when_the_cooperative_one_does_not_help()
    {
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([App]);

        // SoftProtocol (0x0C over the wedged bulk endpoint) changes nothing — exactly the real
        // failure mode. Only the EP0 rescue gets through.
        var reset = new FakeReset([Soft, OutOfBand], (_, strategy) =>
        {
            appSource.Drop("app");
            appSource.Fire(Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" });
            return Task.FromResult(ResetOutcome.Issued);
        });

        var entry = new FakeEntry(BootFilter, _ =>
        {
            bool rescued = reset.Performed.Any(s => s.Kind == ResetKind.SoftProtocolOutOfBand);
            if (!rescued) throw new TimeoutException("still wedged");
            bootSource.Fire(Boot);
            return Task.CompletedTask;
        });

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, App,
            flash: (dev, ct) => Task.FromResult("ok"),
            options: WithRecovery(reset),
            waitSource: Route(bootSource, appSource));

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal(
            [ResetKind.SoftProtocol, ResetKind.SoftProtocolOutOfBand],
            reset.Performed.Select(s => s.Kind));
    }

    // ── The guardrails ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_recovery_configured_the_first_failure_still_fails_the_run_unwrapped()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(BootFilter, _ => throw new TimeoutException("wedged"));

        // No Recovery => the original behaviour exactly: EnterAsync's exception propagates as-is,
        // neither retried nor wrapped.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: new BootloaderEntryOptions { BootloaderTimeout = TimeSpan.FromMilliseconds(150) },
                waitSource: _ => source));
    }

    [Fact]
    public async Task A_refusing_safety_gate_aborts_the_update_and_resets_nothing()
    {
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource();
        var reset = new FakeReset([Soft, OutOfBand]);
        var entry = new FakeEntry(BootFilter, _ => throw new TimeoutException("wedged"));

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset, gate: new GateSaying(false)),
                waitSource: Route(bootSource, appSource)));

        Assert.Contains("safety gate refused", ex.Message);
        Assert.Contains("when the device is idle", ex.Message);
        Assert.Empty(reset.Performed);          // the whole point: no board was disturbed
    }

    [Fact]
    public async Task A_device_that_advertises_no_reset_gives_up_without_pretending_to_recover()
    {
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource();
        var reset = new FakeReset([]);          // not resettable
        var entry = new FakeEntry(BootFilter, _ => throw new TimeoutException("wedged"));

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                waitSource: Route(bootSource, appSource)));

        Assert.Contains("0 reset(s)", ex.Message);
        Assert.Empty(reset.Performed);
    }

    [Fact]
    public async Task Exhausting_the_ladder_reports_what_was_tried_and_names_a_power_cycle()
    {
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([App]);
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");
            appSource.Fire(Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" });
            return Task.FromResult(ResetOutcome.Issued);
        });
        var entry = new FakeEntry(BootFilter, _ => throw new TimeoutException("beyond help"));

        var ex = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                waitSource: Route(bootSource, appSource)));

        Assert.Equal(2, reset.Performed.Count);               // both rungs spent
        Assert.Contains("2 reset(s)", ex.Message);
        Assert.Contains("physical power-cycle", ex.Message);
        Assert.NotNull(ex.InnerException);                    // the original fault is preserved
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_never_mistaken_for_a_device_fault()
    {
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource();
        var reset = new FakeReset([Soft, OutOfBand]);
        using var cts = new CancellationTokenSource();

        var entry = new FakeEntry(BootFilter, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                waitSource: Route(bootSource, appSource),
                ct: cts.Token));

        Assert.Empty(reset.Performed);           // a cancelled run resets nothing
    }

    // ── Identity: recovery must never adopt a device it cannot prove is the same board ───────────

    [Fact]
    public async Task Never_adopts_a_same_model_sibling_that_appears_during_the_return_window()
    {
        // No LocationPath and no serial => no invariant identity. A sibling board of the same model
        // re-enumerating inside our window (ordinary during a concurrent fleet flash) must NOT be
        // adopted as "our" board — adopting it would aim the retry, and then the flash, at the
        // wrong physical hardware. This is the #220 correlation-collapse shape.
        var anonymousApp = Dev("app", 0x10C4, 0x8A7E, location: null) with { SerialNumber = null };
        var sibling = Dev("sibling", 0x10C4, 0x8A7E, location: null) with { SerialNumber = null };

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource();
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Fire(sibling);                    // a different board comes up
            return Task.FromResult(ResetOutcome.Issued);
        });

        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            seenByEnter.Add(d.Id.ToString()!);
            throw new TimeoutException("wedged");
        });

        await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, anonymousApp,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset, correlation: DeviceCorrelationMode.FirstAppearance),
                waitSource: Route(bootSource, appSource)));

        // Every retry stayed pointed at our own board, never the sibling.
        Assert.All(seenByEnter, id => Assert.DoesNotContain("sibling", id));
        Assert.NotEmpty(reset.Performed);               // recovery still ran; it just did not adopt
    }

    [Fact]
    public async Task Does_not_adopt_anything_when_the_platform_reports_no_port()
    {
        // No LocationPath => no identity that survives the re-enumeration window, so recovery must
        // not re-acquire at all. It still resets and retries — against the snapshot it already
        // holds, which is safe because a stale id fails to open rather than resolving to a DIFFERENT
        // board. A sibling appearing meanwhile must never be picked up.
        //
        // A serial is deliberately NOT accepted here even though one is present: uniqueness can only
        // ever be proven among devices present BEFORE the reset, and once our board is off the bus a
        // same-serial sibling would be the only match. Many USB families ship one hardcoded serial
        // across every unit.
        var portless = Dev("app", 0x10C4, 0x8A7E, location: null) with { SerialNumber = "IMNUZ6YW" };
        var sibling = Dev("sibling", 0x10C4, 0x8A7E, location: null) with { SerialNumber = "IMNUZ6YW" };

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([portless]);
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");                  // ours leaves...
            appSource.Fire(sibling);                // ...and a same-serial twin is the only match
            return Task.FromResult(ResetOutcome.Issued);
        });

        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            seenByEnter.Add(d.Id.ToString()!);
            throw new TimeoutException("wedged");
        });

        await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, portless,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset, correlation: DeviceCorrelationMode.FirstAppearance),
                waitSource: Route(bootSource, appSource)));

        Assert.All(seenByEnter, id => Assert.DoesNotContain("sibling", id));
        Assert.NotEmpty(reset.Performed);           // recovery still escalated; it just did not adopt
    }

    [Fact]
    public async Task A_reset_is_still_issued_for_a_device_with_no_identity_at_all()
    {
        // The reset must never be suppressed as a side effect of not being able to re-acquire —
        // a rung that does not need identity (or a device that has none) still gets its reset.
        var anonymous = Dev("app", 0x10C4, 0x8A7E, location: null) with { SerialNumber = null };

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([anonymous]);
        var reset = new FakeReset([Soft, OutOfBand]);
        var entry = new FakeEntry(BootFilter, _ => throw new TimeoutException("wedged"));

        await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, anonymous,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset, correlation: DeviceCorrelationMode.FirstAppearance),
                waitSource: Route(bootSource, appSource)));

        Assert.Equal(
            [ResetKind.SoftProtocol, ResetKind.SoftProtocolOutOfBand],
            reset.Performed.Select(r => r.Kind));   // the full ladder was still spent
    }

    [Fact]
    public async Task Never_adopts_a_different_board_swapped_onto_the_same_port()
    {
        // A LocationPath identifies a slot, not its occupant. If the board is unplugged during the
        // window and another of the same model goes into that port, the port alone would admit it —
        // so the serial is ANDed in, and excludes exactly this.
        var app = Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" };
        var replacement = Dev("replacement", 0x10C4, 0x8A7E) with { SerialNumber = "CDYHINBH" };

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([app]);
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");                      // ours is pulled...
            appSource.Fire(replacement);                // ...and a different board takes the port
            return Task.FromResult(ResetOutcome.Issued);
        });

        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            seenByEnter.Add(d.Id.ToString()!);
            throw new TimeoutException("wedged");
        });

        await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, app,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                waitSource: Route(bootSource, appSource)));

        Assert.All(seenByEnter, id => Assert.DoesNotContain("replacement", id));
        Assert.NotEmpty(reset.Performed);
    }

    [Fact]
    public async Task Adopts_our_own_board_returning_to_its_port_with_its_serial_intact()
    {
        // The complement of the test above: same port AND same serial IS our board, and must be
        // adopted even though its instance id changed case across the re-enumeration (#231).
        var app = Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" };
        var ours = Dev("APP", 0x10C4, 0x8A7E) with { SerialNumber = "IMNUZ6YW" };
        var elsewhere = Dev("elsewhere", 0x10C4, 0x8A7E, location: "PCIROOT(0)#USB(9)")
            with { SerialNumber = "IMNUZ6YW" };   // same serial, wrong port — excluded by the port

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([app]);
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");
            appSource.Fire(elsewhere);                  // fired FIRST and must lose
            appSource.Fire(ours);
            return Task.FromResult(ResetOutcome.Issued);
        });

        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            seenByEnter.Add(d.Id.ToString()!);
            if (reset.Performed.Count == 0) throw new TimeoutException("wedged");
            bootSource.Fire(Boot);
            return Task.CompletedTask;
        });

        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, app,
            flash: (dev, ct) => Task.FromResult("ok"),
            options: WithRecovery(reset),
            waitSource: Route(bootSource, appSource));

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal("APP", seenByEnter[^1]);
    }

    [Fact]
    public async Task Does_not_adopt_on_a_port_match_alone_when_the_board_exposes_no_serial()
    {
        // A port match says a compatible board is in that slot, not that it is OUR board. With no
        // serial to confirm it, a different board inserted into the port during the window would be
        // indistinguishable — so there is no identity at all and nothing is adopted.
        var noSerial = Dev("app", 0x10C4, 0x8A7E) with { SerialNumber = null };
        var replacement = Dev("replacement", 0x10C4, 0x8A7E) with { SerialNumber = null };

        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([noSerial]);
        var reset = new FakeReset([Soft, OutOfBand], (_, _) =>
        {
            appSource.Drop("app");
            appSource.Fire(replacement);            // same port, same VID/PID, different board
            return Task.FromResult(ResetOutcome.Issued);
        });

        var seenByEnter = new List<string>();
        var entry = new FakeEntry(BootFilter, d =>
        {
            seenByEnter.Add(d.Id.ToString()!);
            throw new TimeoutException("wedged");
        });

        await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, noSerial,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                waitSource: Route(bootSource, appSource)));

        Assert.All(seenByEnter, id => Assert.DoesNotContain("replacement", id));
        Assert.NotEmpty(reset.Performed);           // the ladder still ran; only the adopt was refused
    }

    // ── A host-side bug must never disrupt hardware ──────────────────────────────────────────────

    [Fact]
    public async Task A_throwing_progress_consumer_propagates_and_resets_nothing()
    {
        // A disposed IProgress consumer throwing from Report says nothing about the device. An
        // earlier revision caught everything inside the wait and answered it by resetting the board.
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource();
        var reset = new FakeReset([Soft, OutOfBand]);

        var entry = new FakeEntry(BootFilter, _ => Task.CompletedTask);   // entry itself is fine

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset),
                phase: new Progress(_ => throw new ObjectDisposedException("ui")),
                waitSource: Route(bootSource, appSource)));

        Assert.Empty(reset.Performed);
    }

    // ── A reset that outlives the settle delay (periphery #251) ─────────────────────────────────

    // Longer than BootloaderEntryOrchestrator's 750 ms SettleAfterReset, so the settle alone cannot
    // cover it — the point of the pair below. Shorter than the 2 s ReturnTimeout the tests configure.
    private static readonly TimeSpan SlowReload = TimeSpan.FromMilliseconds(1200);

    private static readonly ResetStrategy DisableEnable =
        new(ResetKind.PnpDisableEnable, ResetBlastRadius.Self, ReEnumerates: false);

    /// <summary>
    /// Models the board the field actually had: perfectly healthy, but its driver stack takes
    /// <see cref="SlowReload"/> to come back after a PnP disable/enable. Until then any attempt to
    /// open it fails with "not found" — the device is not gone, it is mid-reload.
    /// </summary>
    private sealed class SlowlyReloadingBoard
    {
        private long _usableAtTicks = long.MaxValue;

        public bool IsUsable => Environment.TickCount64 >= _usableAtTicks;

        /// <summary>The reset lands and the driver stack begins reloading.</summary>
        public void BeginReload() => _usableAtTicks = Environment.TickCount64 + (long)SlowReload.TotalMilliseconds;

        public async Task WaitUntilUsableAsync()
        {
            while (!IsUsable)
                await Task.Delay(10);
        }
    }

    /// <summary>
    /// The #251 regression, stated as a property of the seam: a reset rung that reports success
    /// while the device is still mid-reload gets its very next attempt thrown away, and a board that
    /// is fine is declared beyond recovery.
    /// </summary>
    /// <remarks>
    /// This is why <see cref="IDeviceReset.ResetAsync"/> carries a duty to wait, and why
    /// <c>SettleAfterReset</c> must never be re-tasked as the readiness gate: the orchestrator has no
    /// way to tell "still coming back" from "will never come back", so it charges the attempt budget
    /// for both. The companion test below changes only the reset's waiting behaviour and the same
    /// board flashes.
    /// </remarks>
    [Fact]
    public async Task DisableEnable_thatReturnsBeforeTheDeviceIsUsable_burnsTheAttemptAndFailsAHealthyBoard()
    {
        var board = new SlowlyReloadingBoard();
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([App]);

        var reset = new FakeReset([DisableEnable], (_, _) =>
        {
            board.BeginReload();
            return Task.FromResult(ResetOutcome.Issued);   // returns immediately — the bug
        });

        var entry = new FakeEntry(BootFilter, _ =>
        {
            if (!board.IsUsable)
                throw new InvalidOperationException(
                    "USB device 'USB\\VID_10C4&PID_8A7E\\IMNUZ6YW' was not found - it may have been disconnected.");
            bootSource.Fire(Boot);
            return Task.CompletedTask;
        });

        var fault = await Assert.ThrowsAsync<BootloaderEntryException>(() =>
            BootloaderEntryOrchestrator.RunAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("ok"),
                options: WithRecovery(reset, policy: new EscalatingResetRecoveryPolicy(sanityRetries: 0)),
                waitSource: Route(bootSource, appSource)));

        Assert.Contains("the recovery policy gave up", fault.Message);

        // The board was never lost — exactly what was seen hours later. The update failed on a
        // host-side timing artifact, not on anything wrong with the hardware.
        await board.WaitUntilUsableAsync();
        Assert.True(board.IsUsable);
    }

    /// <summary>
    /// The fix, from the orchestrator's side: when the reset honours its contract and returns only
    /// once the device is back, the same slow board flashes on the retry it was already going to make
    /// — no extra attempt, no extra rung.
    /// </summary>
    [Fact]
    public async Task DisableEnable_thatWaitsForTheDeviceToBeUsable_flashesTheSameBoard()
    {
        var board = new SlowlyReloadingBoard();
        var bootSource = new FakeWaitSource();
        var appSource = new FakeWaitSource([App]);

        var reset = new FakeReset([DisableEnable], async (_, _) =>
        {
            board.BeginReload();
            await board.WaitUntilUsableAsync();           // what WindowsDeviceReset now does
            return ResetOutcome.Issued;
        });

        int entered = 0;
        var entry = new FakeEntry(BootFilter, _ =>
        {
            entered++;
            if (!board.IsUsable)
                throw new InvalidOperationException(
                    "USB device 'USB\\VID_10C4&PID_8A7E\\IMNUZ6YW' was not found - it may have been disconnected.");
            bootSource.Fire(Boot);
            return Task.CompletedTask;
        });

        DeviceInfo? flashed = null;
        var result = await BootloaderEntryOrchestrator.RunAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashed = dev; return Task.FromResult("ok"); },
            options: WithRecovery(reset, policy: new EscalatingResetRecoveryPolicy(sanityRetries: 0)),
            waitSource: Route(bootSource, appSource));

        Assert.Equal("ok", result.FlashResult);
        Assert.Equal("boot", flashed!.Id);

        // The wedged attempt, then the post-reset one. The single advertised rung was enough.
        Assert.Equal(2, entered);
        Assert.Equal([ResetKind.PnpDisableEnable], reset.Performed.Select(s => s.Kind));
    }

    private sealed class Progress(Action<BootloaderEntryPhase> onReport) : IProgress<BootloaderEntryPhase>
    {
        public void Report(BootloaderEntryPhase value) => onReport(value);
    }
}
