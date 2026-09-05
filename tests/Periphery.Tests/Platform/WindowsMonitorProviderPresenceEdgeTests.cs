// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Periphery.Windows;
using Xunit;

namespace Periphery.Tests.Platform;

/// <summary>
/// Presence-edge tests for issue #177. Four are regression guards — they fail against
/// the pre-fix provider. <c>InstanceRemoved_ForANeverAnnouncedDevnode_RaisesNothing</c>
/// is characterisation: it passes on both sides and pins behaviour this change preserves.
///
/// <para>On Windows, <c>DeviceAppeared</c> fired only from the
/// <see cref="DeviceWatcher"/> startup snapshot and never for a live arrival. The provider
/// sourced presence from a <c>CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE</c> registration made
/// with <c>ClassGuid = Guid.Empty</c> and no <c>ALL_INTERFACE_CLASSES</c> flag — i.e. for
/// <c>GUID_NULL</c> — so it registered successfully and then delivered nothing. Presence is
/// now sourced from <c>DEVICEINSTANCEENUMERATED</c> on the instance registration.
///
/// <para>These drive the notification callback directly with a synthesized
/// <c>CM_NOTIFY_EVENT_DATA</c> buffer rather than waiting on a physical hot-plug, so they
/// are deterministic and need no hardware beyond a devnode that already exists on the
/// machine. What they cannot prove is that Windows actually delivers action 7 for a real
/// arrival — that was established by measurement (see issue #177) and is not something a
/// unit test can assert. What they do pin is the mapping from action to event, which is
/// exactly what regressed.</para>
/// </summary>
public class WindowsMonitorProviderPresenceEdgeTests
{
    /// <summary>
    /// Builds the <c>CM_NOTIFY_EVENT_DATA</c> DeviceInstance layout the callback parses:
    /// FilterType (4 bytes), Reserved (4 bytes), then a null-terminated UTF-16 instance id.
    /// </summary>
    private static nint AllocEventData(string instanceId, out int size)
    {
        const int headerBytes = 8;
        size = headerBytes + ((instanceId.Length + 1) * 2);
        nint buffer = Marshal.AllocHGlobal(size);
        for (int i = 0; i < headerBytes; i++)
            Marshal.WriteByte(buffer, i, 0);
        for (int i = 0; i < instanceId.Length; i++)
            Marshal.WriteInt16(buffer, headerBytes + (i * 2), (short)instanceId[i]);
        Marshal.WriteInt16(buffer, headerBytes + (instanceId.Length * 2), 0);
        return buffer;
    }

    private static int Notify(WindowsDeviceMonitorProvider provider, int action, string instanceId)
    {
        var method = typeof(WindowsDeviceMonitorProvider).GetMethod(
            "OnDeviceNotification", BindingFlags.NonPublic | BindingFlags.Instance)!;

        nint buffer = AllocEventData(instanceId, out int size);
        try
        {
            return (int)method.Invoke(provider, [(nint)0, action, buffer, size])!;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// A real devnode from this machine, excluding monitors (they take the ordered
    /// publish path, which needs the display sink a bare provider has not started).
    /// </summary>
    private static async Task<string?> FindRealNonMonitorInstanceIdAsync()
    {
        var devices = await Devices.Enumerate().ToListAsync();
        return devices
            .Where(d => d.Category != DeviceCategory.Monitor)
            .Select(d => d.Id.Value)
            .FirstOrDefault(id => WindowsDeviceProvider.TryBuildDeviceInfo(id) is not null);
    }

    [Fact]
    public async Task InstanceEnumerated_RaisesAppeared_OnceOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? instanceId = await FindRealNonMonitorInstanceIdAsync();
        if (instanceId is null) return;   // no readable devnode on this box

        var provider = new WindowsDeviceMonitorProvider();
        var appeared = new List<DeviceId>();
        provider.DeviceAppeared += (_, e) => appeared.Add(e.Device.Id);

        // First sighting: the devnode entered the tree, so presence is announced.
        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED, instanceId);
        Assert.Single(appeared);

        // Action 7 also fires on re-enumeration of a devnode already known (driver
        // reload, sleep-resume). That is not an arrival and must not re-announce.
        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED, instanceId);
        Assert.Single(appeared);
    }

    [Fact]
    public async Task InstanceStarted_AfterEnumerated_RaisesActivatedWithoutASecondAppeared()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? instanceId = await FindRealNonMonitorInstanceIdAsync();
        if (instanceId is null) return;

        var provider = new WindowsDeviceMonitorProvider();
        var appeared = new List<DeviceId>();
        var activated = new List<DeviceId>();
        provider.DeviceAppeared += (_, e) => appeared.Add(e.Device.Id);
        provider.DeviceActivated += (_, e) => activated.Add(e.Device.Id);

        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED, instanceId);
        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED, instanceId);

        Assert.Single(appeared);
        Assert.Single(activated);
    }

    /// <summary>
    /// The ADR-0004 invariant is that active implies present, and it should hold on the
    /// event stream without depending on cfgmgr32 always delivering action 7 first. A
    /// start for a devnode never announced raises Appeared before Activated.
    /// </summary>
    [Fact]
    public async Task InstanceStarted_WithoutAPriorEnumerated_RaisesAppearedBeforeActivated()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? instanceId = await FindRealNonMonitorInstanceIdAsync();
        if (instanceId is null) return;

        var provider = new WindowsDeviceMonitorProvider();
        var order = new List<string>();
        provider.DeviceAppeared += (_, _) => order.Add("appeared");
        provider.DeviceActivated += (_, _) => order.Add("activated");

        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED, instanceId);

        Assert.Equal(["appeared", "activated"], order);
    }

    /// <summary>
    /// Removal has a single source since the interface registration was removed. A devnode
    /// the cache never held was never announced, so its removal raises nothing — otherwise
    /// a consumer sees a Disappeared with no matching Appeared.
    ///
    /// <para><b>Characterisation, not a regression guard.</b> This passes against the
    /// pre-#177 provider too, whose <c>HandleInstanceRemoved</c> had the same early
    /// return. It pins behaviour the change preserves, so a future single-source-removal
    /// refactor cannot quietly start raising <c>Disappeared</c> for a devnode that was
    /// never announced.</para>
    /// </summary>
    [Fact]
    public void InstanceRemoved_ForANeverAnnouncedDevnode_RaisesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var provider = new WindowsDeviceMonitorProvider();
        var disappeared = new List<DeviceId>();
        provider.DeviceDisappeared += (_, e) => disappeared.Add(e.Device.Id);

        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED,
            @"USB\VID_FFFF&PID_FFFF\periphery-test-never-existed");

        Assert.Empty(disappeared);
    }

    /// <summary>
    /// A monitor's first appearance must carry its DisplayConfig fields.
    ///
    /// <para>The notification path builds through <c>TryBuildDeviceInfo</c>, which does not
    /// run <c>WindowsDisplayConfigEnricher</c> — only <c>EnumerateAsync</c> does. So every
    /// monitor payload built here starts with <c>MonitorName</c>, <c>DisplayResolution</c>,
    /// <c>DisplayBounds</c>, orientation, connector and the luminance fields null.
    /// <c>MergeArrival</c> backfills them from a prior cache entry, which covers a
    /// re-appearance and cannot cover a first one, because there is no prior by definition —
    /// and that is exactly when Appeared is raised. A consumer filtering on
    /// <c>WithMinResolution</c> would have had its only Appeared suppressed.</para>
    ///
    /// <para>Needs a monitor attached, so it no-ops on a headless runner rather than
    /// asserting something it cannot see.</para>
    /// </summary>
    [Fact]
    public async Task MonitorFirstAppearance_CarriesDisplayConfigFields()
    {
        if (!OperatingSystem.IsWindows()) return;

        var devices = await Devices.Enumerate().OfCategory(DeviceCategory.Monitor).ToListAsync();
        var monitor = devices.FirstOrDefault(d => d.DisplayResolution is not null);
        if (monitor is null) return;   // headless, or DisplayConfig unavailable

        var provider = new WindowsDeviceMonitorProvider();
        DeviceInfo? appeared = null;
        provider.DeviceAppeared += (_, e) => appeared = e.Device;

        // Fresh provider, empty cache: this is a first sighting, so there is no prior
        // for MergeArrival to fill from.
        Notify(provider, DevNodeHelper.CM_NOTIFY_ACTION_DEVICEINSTANCEENUMERATED, monitor.Id.Value);

        Assert.NotNull(appeared);
        Assert.NotNull(appeared!.DisplayResolution);
    }

    /// <summary>
    /// Naming-convention tripwire: no instance field of the provider names an interface
    /// registration. It asserts on a field-name substring, so it cannot detect a
    /// registration held under a different name or in a local — it does not prove the
    /// absence of one, it makes the obvious reintroduction loud.
    ///
    /// <para>The behavioural tests above are what catch the defect. This exists as well
    /// because the original bug was invisible at runtime: <c>CM_Register_Notification</c>
    /// returned success and then delivered nothing.</para>
    /// </summary>
    [Fact]
    public void Provider_HoldsNoDeviceInterfaceNotificationRegistration()
    {
        var fields = typeof(WindowsDeviceMonitorProvider)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.Name)
            .ToList();

        Assert.DoesNotContain(fields, n => n.Contains("interface", StringComparison.OrdinalIgnoreCase));
    }
}
