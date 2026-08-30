using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Firmware;
using Xunit;

namespace Periphery.Bootloader.Tests;

/// <summary>The app-mode entry registry and the format-converter registry (slice 3 resolution).</summary>
public class RegistryTests
{
    private static DeviceInfo Dev(string id) => new() { Id = id };

    private sealed class FakeEntry(string name, Func<DeviceInfo, bool> canEnter) : IBootloaderEntry
    {
        public string Name => name;
        public bool CanEnter(DeviceInfo d) => canEnter(d);
        public DeviceFilter ExpectedBootloader { get; } = new DeviceFilter().WithUsbId("10C4", "EAC9");
        public Task EnterAsync(DeviceInfo d, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConverter(FirmwareFormat source, FirmwareFormat target) : IFirmwareConverter
    {
        public FirmwareFormat Source => source;
        public FirmwareFormat Target => target;
        public FirmwarePayload Convert(ReadOnlyMemory<byte> content) => FirmwarePayload.FromBlob(content, target);
    }

    // ── BootloaderEntryRegistry ────────────────────────────────────────────────

    [Fact]
    public void EntryRegistry_matches_the_first_capable_entry_in_registration_order()
    {
        var registry = new BootloaderEntryRegistry();
        registry.Register(new FakeEntry("first", d => d.Id == "x"));
        registry.Register(new FakeEntry("second", d => d.Id == "x")); // also matches, but registered later

        Assert.Equal("first", registry.Match(Dev("x"))!.Name);
    }

    [Fact]
    public void EntryRegistry_returns_null_when_no_entry_matches()
    {
        var registry = new BootloaderEntryRegistry();
        registry.Register(new FakeEntry("treehopper", d => d.Id == "app"));
        Assert.Null(registry.Match(Dev("something-else")));
    }

    // ── FirmwareConverterRegistry ──────────────────────────────────────────────

    [Fact]
    public void ConverterRegistry_finds_a_converter_to_an_accepted_target()
    {
        var registry = new FirmwareConverterRegistry();
        registry.Register(new FakeConverter(FirmwareFormat.IntelHex, FirmwareFormat.Efm8BootRecords));

        var found = registry.Find(FirmwareFormat.IntelHex, new[] { FirmwareFormat.Efm8BootRecords });
        Assert.NotNull(found);
        Assert.Equal(FirmwareFormat.Efm8BootRecords, found!.Target);
    }

    [Fact]
    public void ConverterRegistry_returns_null_when_no_accepted_target_matches()
    {
        var registry = new FirmwareConverterRegistry();
        registry.Register(new FakeConverter(FirmwareFormat.IntelHex, FirmwareFormat.Efm8BootRecords));

        // The source matches, but the target (Efm8BootRecords) is not among the accepted formats.
        Assert.Null(registry.Find(FirmwareFormat.IntelHex, new[] { FirmwareFormat.RawBinary }));
    }

    [Fact]
    public void ConverterRegistry_returns_null_for_a_different_source()
    {
        var registry = new FirmwareConverterRegistry();
        registry.Register(new FakeConverter(FirmwareFormat.IntelHex, FirmwareFormat.Efm8BootRecords));
        Assert.Null(registry.Find(FirmwareFormat.Elf, new[] { FirmwareFormat.Efm8BootRecords }));
    }
}
