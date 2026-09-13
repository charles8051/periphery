using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Testing;
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
        private readonly Queue<DeviceInfo> _onNextStart = new();

        public event Action<DeviceInfo>? Appeared;
        public event Action<string>? Disappeared;
        public Task StartAsync(CancellationToken ct)
        {
            if (snapshot is not null)
                foreach (var d in snapshot) Appeared?.Invoke(d);
            while (_onNextStart.TryDequeue(out var d))
                Appeared?.Invoke(d);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Fire(DeviceInfo d) => Appeared?.Invoke(d);

        /// <summary>
        /// The device comes back, and is present when the next wait subscribes. The app-liveness wait
        /// (RunAsync's ApplicationFilter step) has no afterArm hook and accepts a pre-existing match, so
        /// a flash or verify callback that brings the application back queues it here. It is delivered
        /// by that wait's own StartAsync, ahead of the deadline the wait arms after it.
        /// </summary>
        /// <remarks>
        /// This replaced firing from a background task 20 ms later, which raced the orchestrator's
        /// subscription against its 200 ms timeout on a real clock (periphery#1).
        /// </remarks>
        public void AppearOnNextStart(DeviceInfo d) => _onNextStart.Enqueue(d);
    }

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
    //
    // The timeouts are on the test's fake clock and an hour long, so only that clock can end a wait:
    // a deadline armed on the system timer fails a test at RunAdvancingAsync's bound instead of
    // passing late (ADR-0089).
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromHours(1);

    private static BootloaderEntryOptions Options(TimeProvider? time = null) => new()
    {
        ApplicationFilter = new DeviceFilter().WithUsbId("10C4", "8A7E"),
        BootloaderTimeout = WaitTimeout,
        ApplicationTimeout = WaitTimeout,
        TimeProvider = time ?? TimeProvider.System,
    };

    [Fact]
    public async Task FirstAttemptMatches_ReportsVerifiedOnAttemptOne()
    {
        var source = new FakeWaitSource();
        var entry = new FakeEntry(source);
        int flashCalls = 0, verifyCalls = 0;

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; source.AppearOnNextStart(App); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(time),
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) =>
            {
                verifyCalls++;
                source.AppearOnNextStart(App);
                return Task.FromResult(verifyCalls > 1); // mismatch first, match second
            },
            flashSucceeded: static _ => true,
            options: Options(time),
            maxAttempts: 3,
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; source.AppearOnNextStart(App); return Task.FromResult(false); },
            flashSucceeded: static _ => true,
            options: Options(time),
            maxAttempts: 3,
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => Task.FromResult("failed"), // no App fire - the device never returns either
            verify: (dev, ct) => { verifyCalled = true; return Task.FromResult(true); },
            flashSucceeded: static r => r == "flashed", // "failed" does not satisfy this
            options: Options(time),
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => Task.FromResult("flashed"), // succeeds, but never fires App back
            verify: (dev, ct) => { verifyCalled = true; return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: Options(time),
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => Task.FromResult(true), // content matches, but never fires App back
            flashSucceeded: static _ => true,
            options: Options(time),
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { flashCalls++; source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { verifyCalls++; return Task.FromResult(false); }, // mismatch, no App fire
            flashSucceeded: static _ => true,
            options: Options(time),
            maxAttempts: 3,
            waitSource: _ => source));

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

        var time = new TimerSignalingFakeTimeProvider();
        var result = await time.RunAdvancingAsync(() => BootloaderEntryOrchestrator.RunWithVerificationAsync<string>(
            entry, App,
            flash: (dev, ct) => { source.AppearOnNextStart(App); return Task.FromResult("flashed"); },
            verify: (dev, ct) => { source.AppearOnNextStart(App); return Task.FromResult(true); },
            flashSucceeded: static _ => true,
            options: new BootloaderEntryOptions
            {
                BootloaderTimeout = WaitTimeout,
                ApplicationTimeout = WaitTimeout,
                TimeProvider = time,
            }, // ApplicationFilter deliberately omitted
            waitSource: _ => source));

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
