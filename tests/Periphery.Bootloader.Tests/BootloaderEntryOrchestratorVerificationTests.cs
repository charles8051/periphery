using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>
/// Tests for <see cref="BootloaderEntryOrchestrator.RunWithVerificationAsync{TResult}"/> (periphery#246):
/// flash, independently re-verify in a fresh bootloader session, retry the whole flash on a
/// mismatch — built from two <see cref="BootloaderEntryOrchestrator.RunAsync{TResult}"/> calls, no
/// hardware, driven by a fake wait source exactly like <see cref="BootloaderEntryOrchestratorTests"/>.
/// </summary>
public class BootloaderEntryOrchestratorVerificationTests
{
    private static DeviceInfo Dev(string id, ushort vid, ushort pid) =>
        new() { Id = id, VendorId = new HardwareId(vid), ProductId = new HardwareId(pid) };

    private static readonly DeviceInfo App = Dev("app", 0x10C4, 0x8A7E);
    private static readonly DeviceInfo Boot = Dev("boot", 0x10C4, 0xEAC9);

    // Shared with BootloaderEntryOrchestratorTests's own copy: a single instance is reused across
    // every RunAsync call RunWithVerificationAsync makes (its own DisposeAsync is a no-op, so
    // "await using" disposing it after each inner call is harmless), letting one test orchestrate a
    // whole flash -> verify -> retry sequence by firing events at the right narrative moments.
    private sealed class FakeWaitSource(IEnumerable<DeviceInfo>? snapshot = null) : IDeviceWaitSource
    {
        public event Action<DeviceInfo>? Appeared;
        public event Action<string>? Disappeared;
        public Task StartAsync(CancellationToken ct)
        {
            if (snapshot is not null)
                foreach (var d in snapshot) Appeared?.Invoke(d);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Fire(DeviceInfo d) => Appeared?.Invoke(d);
    }

    // The app-liveness wait (RunAsync's ApplicationFilter step) has no afterArm hook — production
    // relies on the OS device watcher firing Appeared truly asynchronously, sometime after the wait
    // subscribes. A synchronous Fire() call from inside the flash/verify callback runs and returns
    // BEFORE that wait even starts listening, so the event would be lost. This schedules the fire on
    // a background task with a short delay instead, comfortably ahead of the tests' 200ms timeouts.
    private static void FireShortly(FakeWaitSource source, DeviceInfo device)
        => _ = Task.Run(async () => { await Task.Delay(20); source.Fire(device); });

    // EnterAsync always just reboots into the bootloader (fires Boot) - a real device re-entering a
    // second time for the verify round behaves identically to the first.
    private sealed class FakeEntry(FakeWaitSource source) : IBootloaderEntry
    {
        public string Name => "Fake";
        public bool CanEnter(DeviceInfo d) => d.VendorId == App.VendorId && d.ProductId == App.ProductId;
        public DeviceFilter ExpectedBootloader { get; } = new DeviceFilter().WithUsbId("10C4", "EAC9");
        public Task EnterAsync(DeviceInfo d, CancellationToken ct)
        {
            source.Fire(Boot);
            return Task.CompletedTask;
        }
    }

    // Common options: ApplicationFilter is required for RunWithVerificationAsync to know when it is
    // safe to re-enter for a verify pass.
    private static BootloaderEntryOptions Options() => new()
    {
        ApplicationFilter = new DeviceFilter().WithUsbId("10C4", "8A7E"),
        BootloaderTimeout = TimeSpan.FromMilliseconds(200),
        ApplicationTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task FirstAttemptMatches_ReportsVerifiedOnAttemptOne()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        int flashCalls = 0, verifyCalls = 0;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; FireShortly(source, App); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(),
            waitSource: _ => source);

        Assert.Equal("flashed", result.FlashResult);
        Assert.True(result.Verified);
        Assert.True(result.ApplicationReturned);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, flashCalls);
        Assert.Equal(1, verifyCalls);
    }

    [Fact]
    public async Task MismatchThenMatch_RetriesTheWholeFlashAndReportsAttemptTwo()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        int flashCalls = 0, verifyCalls = 0;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) =>
            {
                verifyCalls++;
                FireShortly(source, App);
                return Task.FromResult(verifyCalls > 1); // mismatch first, match second
            },
            flashSucceeded: static _ => true,
            options: Options(),
            maxAttempts: 3,
            waitSource: _ => source);

        Assert.True(result.Verified);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, flashCalls);  // the whole flash re-ran, not just the verify
        Assert.Equal(2, verifyCalls);
    }

    [Fact]
    public async Task PersistentMismatch_ExhaustsMaxAttemptsAndReportsNotVerified()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        int flashCalls = 0, verifyCalls = 0;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; FireShortly(source, App); return Task.FromResult(false); },
            flashSucceeded: static _ => true,
            options: Options(),
            maxAttempts: 3,
            waitSource: _ => source);

        Assert.False(result.Verified);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, flashCalls);
        Assert.Equal(3, verifyCalls);
    }

    [Fact]
    public async Task FlashItselfFails_NeverCallsVerifyAndReportsAttemptOne()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        bool verifyCalled = false;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => Task.FromResult("failed"), // no App fire - the device never returns either
            verify: (dev, ct) => { verifyCalled = true; return Task.FromResult(true); },
            flashSucceeded: static r => r == "flashed", // "failed" does not satisfy this
            options: Options(),
            waitSource: _ => source);

        Assert.False(result.Verified);
        Assert.Equal(1, result.Attempts);
        Assert.False(verifyCalled);
    }

    [Fact]
    public async Task ApplicationNeverReturnsAfterFlash_NeverCallsVerify()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        bool verifyCalled = false;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => Task.FromResult("flashed"), // succeeds, but never fires App back
            verify: (dev, ct) => { verifyCalled = true; return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(),
            waitSource: _ => source);

        Assert.False(result.Verified);
        Assert.False(result.ApplicationReturned);
        Assert.Equal(1, result.Attempts);
        Assert.False(verifyCalled); // re-entering without proof of the returned device is refused
    }

    [Fact]
    public async Task VerifyContentMatchesButApplicationNeverConfirmsReturning_ReportsNotVerified()
    {
        // Efm8VerifyOperation's own leave-transfer (RunAppOnly) can be rejected or stall even when
        // the content check itself matched. Verified must require BOTH - a content match alone is
        // not proof the board actually left the bootloader.
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => Task.FromResult(true), // content matches, but never fires App back
            flashSucceeded: static _ => true,
            options: Options(),
            waitSource: _ => source);

        Assert.False(result.Verified);
        Assert.False(result.ApplicationReturned);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task MismatchAndApplicationNeverConfirmsReturning_StopsRatherThanRetryingAStaleDevice()
    {
        // A mismatch whose OWN leave also failed to confirm the app's return leaves no fresh,
        // confirmed device to safely re-enter with. Retrying against the pre-verify snapshot would
        // risk re-entering a device that never actually came back, or (given only a USB-id filter,
        // not a true identity) a different physical board - so this must stop at attempt 1, not
        // exhaust every remaining attempt against an unconfirmed snapshot.
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        int flashCalls = 0, verifyCalls = 0;

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; return Task.FromResult(false); }, // mismatch, no App fire
            flashSucceeded: static _ => true,
            options: Options(),
            maxAttempts: 3,
            waitSource: _ => source);

        Assert.False(result.Verified);
        Assert.False(result.ApplicationReturned);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, flashCalls);
        Assert.Equal(1, verifyCalls);
    }

    [Fact]
    public async Task NoApplicationFilterSupplied_DerivesOneFromTheApplicationDevicesUsbId()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);

        var result = await BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { FireShortly(source, App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { FireShortly(source, App); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: new BootloaderEntryOptions
            {
                BootloaderTimeout = TimeSpan.FromMilliseconds(200),
                ApplicationTimeout = TimeSpan.FromMilliseconds(200),
            }, // ApplicationFilter deliberately omitted
            waitSource: _ => source);

        Assert.True(result.Verified);
        Assert.True(result.ApplicationReturned);
    }

    [Fact]
    public async Task ZeroMaxAttempts_Throws()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
                entry, App,
                flash: (dev, ct) => Task.FromResult("flashed"),
                verify: (dev, ct) => Task.FromResult(true),
                flashSucceeded: static _ => true,
                options: Options(),
                maxAttempts: 0,
                waitSource: _ => source));
    }

    [Fact]
    public async Task NoApplicationFilterAndNoVendorId_ThrowsBeforeAnyDeviceInteraction()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        var noVendorId = new DeviceInfo { Id = "app-no-vid" };
        bool flashCalled = false;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
                entry, noVendorId,
                flash: (dev, ct) => { flashCalled = true; return Task.FromResult("flashed"); },
                verify: (dev, ct) => Task.FromResult(true),
                flashSucceeded: static _ => true,
                options: new BootloaderEntryOptions(), // no ApplicationFilter, and the device has no VendorId
                waitSource: _ => source));

        Assert.Contains("VendorId", ex.Message);
        Assert.False(flashCalled);
    }
}
