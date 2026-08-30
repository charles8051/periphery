using System.Runtime.Versioning;
using Periphery.Windows;

namespace Periphery.Tests;

[SupportedOSPlatform("windows")]
public class WindowsDeviceProviderTests
{
    [Fact]
    public void ResolveCategory_MediaGuid_MapsToAudio()
    {
        var category = WindowsCategoryMap.ResolveCategory(DeviceClassGuids.Media);

        Assert.Equal(DeviceCategory.Audio, category);
    }

    [Fact]
    public void ParseUsbClassCodeFromCompatibleIds_WithMidiCompatibleId_ParsesCode()
    {
        var code = WindowsDeviceProvider.ParseUsbClassCodeFromCompatibleIds(
            new[] { "USB\\Class_01&SubClass_03&Prot_00" });

        Assert.Equal(UsbClassCode.AudioClass.MidiStreaming, code);
    }

    [Fact]
    public void ParseUsbClassCode_WithHardwareIdFallback_ParsesCode()
    {
        var code = WindowsDeviceProvider.ParseUsbClassCode(
            compatibleIds: null,
            hardwareIds: new[] { "USB\\Class_01&SubClass_03&Prot_00" },
            deviceId: null,
            pnpDeviceId: null);

        Assert.Equal(UsbClassCode.AudioClass.MidiStreaming, code);
    }

    [Fact]
    public void ParseUsbClassCode_WithDeviceIdFallback_ParsesCode()
    {
        var code = WindowsDeviceProvider.ParseUsbClassCode(
            compatibleIds: null,
            hardwareIds: null,
            deviceId: "USB\\Class_01&SubClass_03&Prot_00",
            pnpDeviceId: null);

        Assert.Equal(UsbClassCode.AudioClass.MidiStreaming, code);
    }

    [Fact]
    public void ParseUsbClassCodeFromCompatibleIds_WithNonUsbCompatibleId_ReturnsNull()
    {
        var code = WindowsDeviceProvider.ParseUsbClassCodeFromCompatibleIds(
            new[] { "SWD\\MMDEVAPI" });

        Assert.Null(code);
    }

    [Fact]
    public void ParseUsbClassCodeFromCompatibleIds_PrefersMostSpecificMatch()
    {
        var code = WindowsDeviceProvider.ParseUsbClassCodeFromCompatibleIds(
            new[]
            {
                "USB\\Class_01",
                "USB\\Class_01&SubClass_03&Prot_00",
            });

        Assert.Equal(UsbClassCode.AudioClass.MidiStreaming, code);
    }

    // ── LocationPath parent-chain resolution (topology correlation on hardware) ──────────────────
    //
    // Hardware regression: the EFM8 bootloader is opened as its HID function node
    // (HID\VID_10C4&PID_EAC9\…), whose DEVPKEY_Device_LocationPaths is EMPTY, so the old code fell back
    // to the instance id and ByLocationPath never matched the app device's real port — both concurrent
    // flashes timed out with "did not re-enumerate". The port lives on the HID node's USB-node parent
    // and is identical across the app↔bootloader reset, so resolving it there makes correlation match.

    // A fake parent chain: instance id -> (own LocationPaths, parent instance id).
    private static Func<string, (string[]? LocationPaths, string? ParentId)?> Chain(
        params (string Id, string[]? LocationPaths, string? ParentId)[] nodes)
        => id =>
        {
            foreach (var n in nodes)
                if (n.Id == id) return (n.LocationPaths, n.ParentId);
            return null;
        };

    // The board's real physical ports; app and its bootloader share one, and two boards differ.
    private const string PortA = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(2)";
    private const string PortB = "PCIROOT(20)#PCI(0301)#PCI(0000)#USBROOT(0)#USB(6)#USB(3)";

    [Fact]
    public void ResolveLocationPath_HidNodeWithEmptyOwnPath_ResolvesToParentUsbNodePort()
    {
        // The EFM8 bootloader as a HID function node: own LocationPaths empty; parent is its USB node,
        // which carries the port. Must resolve to the USB node's port so ByLocationPath can match.
        const string hidId = "HID\\VID_10C4&PID_EAC9\\8&126BA2DD&0&0000";
        const string usbId = "USB\\VID_10C4&PID_EAC9\\7&E0AA284&0&3";

        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            hidId, ownLocationPaths: null, parentId: usbId,
            Chain((usbId, new[] { PortB }, ParentId: "USB\\ROOT_HUB30\\4&x")));

        Assert.Equal(PortB, resolved);
    }

    [Fact]
    public void ResolveLocationPath_ResolvedBootloaderPort_CorrelatesToTheAppOnThatPort()
    {
        // The end-to-end shape of the hardware bug: two boards on distinct ports. The app node carries
        // its port directly; each bootloader HID node has an empty own path and resolves via its USB-node
        // parent. The resolved bootloader port equals its OWN app's port and differs from the other's —
        // which is exactly the equality ByLocationPath correlation performs, so each board matches itself.
        var appA = WindowsDeviceProvider.ResolveLocationPath(
            "USB\\VID_10C4&PID_8A7E\\A", ownLocationPaths: new[] { PortA }, parentId: null, Chain());
        var bootA = WindowsDeviceProvider.ResolveLocationPath(
            "HID\\VID_10C4&PID_EAC9\\A", ownLocationPaths: null, parentId: "USB\\VID_10C4&PID_EAC9\\A-usb",
            Chain(("USB\\VID_10C4&PID_EAC9\\A-usb", new[] { PortA }, null)));

        var appB = WindowsDeviceProvider.ResolveLocationPath(
            "USB\\VID_10C4&PID_8A7E\\B", ownLocationPaths: new[] { PortB }, parentId: null, Chain());
        var bootB = WindowsDeviceProvider.ResolveLocationPath(
            "HID\\VID_10C4&PID_EAC9\\B", ownLocationPaths: null, parentId: "USB\\VID_10C4&PID_EAC9\\B-usb",
            Chain(("USB\\VID_10C4&PID_EAC9\\B-usb", new[] { PortB }, null)));

        Assert.Equal(appA, bootA);          // board A's bootloader correlates to board A's app
        Assert.Equal(appB, bootB);          // board B's bootloader correlates to board B's app
        Assert.NotEqual(bootA, bootB);      // and the two boards never cross-correlate
    }

    [Fact]
    public void ResolveLocationPath_OwnPathPresent_IsReturnedWithoutWalking()
    {
        // No-op when the node already carries its port: the populated case must not regress, and the
        // parent lookup must not even be consulted (would throw if it were).
        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            "USB\\VID_10C4&PID_8A7E\\A", ownLocationPaths: new[] { PortA }, parentId: "unused",
            lookupNode: _ => throw new Xunit.Sdk.XunitException("parent lookup must not run when own path is present"));

        Assert.Equal(PortA, resolved);
    }

    [Fact]
    public void ResolveLocationPath_WalksMultipleHops_UntilAPortIsFound()
    {
        // Tolerate deeper function/interface layering: walk past an intermediate parent that also lacks
        // a port until an ancestor carries one.
        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            "leaf", ownLocationPaths: null, parentId: "mid",
            Chain(
                ("mid", LocationPaths: null, ParentId: "usb"),
                ("usb", new[] { PortA }, ParentId: "hub")));

        Assert.Equal(PortA, resolved);
    }

    [Fact]
    public void ResolveLocationPath_NoPortAnywhere_FallsBackToInstanceId()
    {
        // A genuinely port-less device with no port up the chain keeps the prior behavior (instance id).
        const string id = "ROOT\\SYSTEM\\0000";
        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            id, ownLocationPaths: null, parentId: "p",
            Chain(("p", LocationPaths: null, ParentId: null)));

        Assert.Equal(id, resolved);
    }

    [Fact]
    public void ResolveLocationPath_MissingParentNode_FallsBackToInstanceId()
    {
        const string id = "HID\\VID_10C4&PID_EAC9\\orphan";
        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            id, ownLocationPaths: null, parentId: "USB\\gone", Chain()); // parent not in the chain

        Assert.Equal(id, resolved);
    }

    [Fact]
    public void ResolveLocationPath_CyclicChain_IsBoundedAndFallsBack()
    {
        // A pathological cycle must not loop forever; the depth cap ends the walk and falls back.
        const string id = "leaf";
        var resolved = WindowsDeviceProvider.ResolveLocationPath(
            id, ownLocationPaths: null, parentId: "a",
            Chain(
                ("a", LocationPaths: null, ParentId: "b"),
                ("b", LocationPaths: null, ParentId: "a")));

        Assert.Equal(id, resolved);
    }

    [Theory]
    [InlineData(0x08, 50, true, BatteryStatus.Charging)]
    [InlineData(0x01, 100, true, BatteryStatus.Full)]
    [InlineData(0x01, 70, true, BatteryStatus.NotCharging)]
    [InlineData(0x01, 70, false, BatteryStatus.Discharging)]
    [InlineData(255, 70, true, BatteryStatus.Unknown)]
    public void MapBatteryStatus_MapsExpectedStates(
        byte batteryFlag,
        int? batteryPercent,
        bool? isExternalPowerConnected,
        BatteryStatus expected)
    {
        var actual = WindowsBatteryEnricher.MapBatteryStatus(
            batteryFlag,
            batteryPercent,
            isExternalPowerConnected);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BatteryEnricher_EnrichesBatteryCategoryDevice()
    {
        var device = new DeviceInfo
        {
            Id = "BATTERY\\TEST",
            Category = DeviceCategory.Battery,
            Name = "Battery",
        };

        var snapshot = new WindowsBatteryEnricher.BatterySnapshot(
            BatteryChargePercent: 77,
            BatteryStatus: Periphery.BatteryStatus.NotCharging,
            IsExternalPowerConnected: true,
            IsBatteryLow: false);

        var enriched = WindowsBatteryEnricher.Enrich(device, snapshot);

        Assert.Equal(77, enriched.BatteryChargePercent);
        Assert.Equal(Periphery.BatteryStatus.NotCharging, enriched.BatteryStatus);
        Assert.True(enriched.IsExternalPowerConnected);
        Assert.False(enriched.IsBatteryLow);
    }

    [Fact]
    public void BatteryEnricher_PropagatesIsBatteryLowTrue()
    {
        // A discharging battery below the critical threshold should
        // surface both facts: BatteryStatus = Discharging AND IsBatteryLow = true.
        // Orthogonal axes (flow direction vs. charge threshold).
        var device = new DeviceInfo
        {
            Id = "BATTERY\\LOW",
            Category = DeviceCategory.Battery,
            Name = "Battery",
        };

        var snapshot = new WindowsBatteryEnricher.BatterySnapshot(
            BatteryChargePercent: 4,
            BatteryStatus: Periphery.BatteryStatus.Discharging,
            IsExternalPowerConnected: false,
            IsBatteryLow: true);

        var enriched = WindowsBatteryEnricher.Enrich(device, snapshot);

        Assert.Equal(4, enriched.BatteryChargePercent);
        Assert.Equal(Periphery.BatteryStatus.Discharging, enriched.BatteryStatus);
        Assert.False(enriched.IsExternalPowerConnected);
        Assert.True(enriched.IsBatteryLow);
    }

    [Fact]
    public void BatteryEnricher_DoesNotChangeNonBatteryCategoryDevice()
    {
        var device = new DeviceInfo
        {
            Id = "USB\\TEST",
            Category = DeviceCategory.Usb,
            Name = "USB Device",
        };

        var snapshot = new WindowsBatteryEnricher.BatterySnapshot(
            BatteryChargePercent: 77,
            BatteryStatus: Periphery.BatteryStatus.NotCharging,
            IsExternalPowerConnected: true,
            IsBatteryLow: false);

        var enriched = WindowsBatteryEnricher.Enrich(device, snapshot);

        Assert.Null(enriched.BatteryChargePercent);
        Assert.Null(enriched.BatteryStatus);
        Assert.Null(enriched.IsExternalPowerConnected);
        Assert.Null(enriched.IsBatteryLow);
    }
}
