using System.Threading.Channels;

namespace Periphery.Usb.Browser.Tests;

/// <summary>
/// The document's registry is reconciled one <c>getDevices()</c> call at a time, so an older
/// snapshot can never be applied over a newer one.
/// </summary>
public class WebUsbGrantSyncTests
{
    // Bounds a failure only.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly UsbGrantFacts Board = new() { VendorId = 0x10C4, ProductId = 0x8A7E, SerialNumber = "AB12" };

    private sealed class FakeGetDevices
    {
        private readonly Channel<TaskCompletionSource<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>> _calls =
            Channel.CreateUnbounded<TaskCompletionSource<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>>();

        public int Count => _calls.Reader.Count;

        public Task<IReadOnlyList<(GrantHandle, UsbGrantFacts)>> Call()
        {
            var call = new TaskCompletionSource<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Writer.TryWrite(call);
            return call.Task;
        }

        /// <summary>Completes with the next call made, including one made before.</summary>
        public Task<TaskCompletionSource<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>> NextAsync()
            => _calls.Reader.ReadAsync().AsTask().WaitAsync(Bound);
    }

    [Fact]
    public async Task A_second_sync_waits_for_the_first_before_asking_for_devices()
    {
        var getDevices = new FakeGetDevices();
        var sync = new WebUsbGrantSync(getDevices.Call);

        var first = sync.SyncAsync();
        var second = sync.SyncAsync();

        // Both calls above return synchronously; only the first got past the gate.
        Assert.Equal(1, getDevices.Count);

        // The first snapshot is from before a replug; the second, taken after it, has the new handle.
        (await getDevices.NextAsync()).SetResult([(new GrantHandle(1), Board)]);
        await first.WaitAsync(Bound);
        (await getDevices.NextAsync()).SetResult([(new GrantHandle(2), Board)]);
        var registry = await second.WaitAsync(Bound);

        var grant = Assert.Single(registry.Grants.Values);
        Assert.Equal(new GrantHandle(2), grant.Handle);
        Assert.Equal("webusb:10C4:8A7E:AB12", grant.Id.Value);
        Assert.Same(registry, sync.Current);
    }

    [Fact]
    public async Task A_failed_snapshot_leaves_the_registry_and_frees_the_next_sync()
    {
        int calls = 0;
        var sync = new WebUsbGrantSync(() => ++calls == 1
            ? Task.FromException<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>(new UsbException("refused"))
            : Task.FromResult<IReadOnlyList<(GrantHandle, UsbGrantFacts)>>([(new GrantHandle(1), Board)]));

        await Assert.ThrowsAsync<UsbException>(() => sync.SyncAsync().WaitAsync(Bound));
        Assert.Same(UsbGrantRegistry.Empty, sync.Current);

        var registry = await sync.SyncAsync().WaitAsync(Bound);
        Assert.Single(registry.Grants);
    }
}
