// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Periphery.Windows;

/// <summary>
/// Windows implementation of <see cref="IDeviceProvider"/> using SetupAPI
/// and cfgmgr32 P/Invoke for device enumeration and property retrieval.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDeviceProvider : IDeviceProvider
{
    private static readonly ILogger<WindowsDeviceProvider> _logger =
        PeripheryLoggerFactory.CreateLogger<WindowsDeviceProvider>();

    private readonly WindowsProviderOptions _options;

    internal WindowsDeviceProvider(WindowsProviderOptions? options = null)
    {
        _options = options ?? new WindowsProviderOptions();
    }

    /// <summary>CM_PROB_DISABLED — the device was disabled via Device Manager / policy.</summary>
    private const int CM_PROB_DISABLED = 0x16;

public async IAsyncEnumerable<DeviceInfo> EnumerateAsync(
    DeviceFilter filter,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    _logger.LogDebug("Starting device enumeration via SetupAPI/cfgmgr32");

    int deviceCount = 0;
    int skippedCount = 0;

    // Push category filter down to SetupAPI class GUID selection
    Guid[]? classGuids = null;
    if (filter.Category.HasValue && filter.Category.Value != DeviceCategory.All)
    {
        string[] guidStrings = WindowsCategoryMap.GetClassGuids(filter.Category.Value);
        classGuids = new Guid[guidStrings.Length];
        for (int i = 0; i < guidStrings.Length; i++)
            classGuids[i] = Guid.Parse(guidStrings[i]);
    }

    _logger.LogInformation("Enumerating devices via SetupAPI (class filter: {ClassFilter})",
        classGuids is null ? "all" : $"{classGuids.Length} GUIDs");

    // Tier 3: DisplayConfig enrichment — built once per enumeration, runs only
    // when the filter can match Monitor devices. Synchronous Win32 calls;
    // no Windows-specific TFM required. See ADR-0018.
    WindowsDisplayConfigEnricher? displayConfigEnricher = null;
    if (filter.NeedsMonitorEnrichment)
    {
        try
        {
            displayConfigEnricher = WindowsDisplayConfigEnricher.Build();
            _logger.LogDebug("DisplayConfig enricher built successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayConfig enricher build failed; monitor properties will be limited");
        }
    }

    // Tier 3: Battery enrichment — read system power snapshot once per
    // enumeration and apply to Battery-category devices.
    WindowsBatteryEnricher.BatterySnapshot? batterySnapshot = null;
    if (filter.NeedsBatteryEnrichment)
    {
        try
        {
            batterySnapshot = WindowsBatteryEnricher.TryReadSnapshot();
            if (batterySnapshot is not null)
                _logger.LogDebug("Battery enricher snapshot captured");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Battery enricher snapshot failed; battery properties will be limited");
        }
    }

    foreach (var (devInst, instanceId) in DevNodeHelper.EnumerateDeviceInstances(classGuids))
    {
        ct.ThrowIfCancellationRequested();

        DeviceInfo? device = null;
        try
        {
            device = ToDeviceInfo(devInst, instanceId);
            device = WindowsNetworkEnricher.Enrich(device);       // Tier 2: MacAddress/IPAddresses/Network
            device = WindowsBatteryEnricher.Enrich(device, batterySnapshot); // Tier 3: Battery charge/status/power source
            if (displayConfigEnricher is not null)
                device = displayConfigEnricher.Enrich(device);    // Tier 3: MonitorName/Resolution/Connector
            // Tier 4: Cross-cutting registered enrichers (ADR-0024 §3c).
            // Extension packages (e.g. Periphery.Hid) self-register via
            // a [ModuleInitializer]; the pipeline iterates the registry
            // snapshot per device and runs each one whose CanEnrich passes.
            device = await EnrichmentPipeline.RunRegisteredAsync(device, ct, _logger)
                .ConfigureAwait(false);
            deviceCount++;
        }
        catch (Exception ex)
        {
            skippedCount++;
            _logger.LogWarning(ex,
                "Failed to parse device {InstanceId}, skipping. Total skipped: {SkippedCount}",
                instanceId, skippedCount);

            System.Diagnostics.Debug.WriteLine(
                $"Failed to parse device {instanceId}: {ex.Message}");
        }

        if (device is not null)
            yield return device;
    }

    _logger.LogInformation(
        "Device enumeration completed. Found: {DeviceCount}, Skipped: {SkippedCount}",
        deviceCount, skippedCount);
}

    /// <summary>
    /// Builds a <see cref="DeviceInfo"/> from a device node handle and instance ID.
    /// </summary>
    internal static DeviceInfo ToDeviceInfo(int devInst, string instanceId)
    {
        string? friendlyName = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_FriendlyName);
        string? description = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_DeviceDesc);
        string? manufacturer = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_Manufacturer);
        string? service = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_Service);
        string? rawClassName = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_Class);
        Guid? classGuid = DevNodeHelper.GetGuidProperty(devInst, in DevNodeHelper.DEVPKEY_Device_ClassGuid);
        Guid? containerId = DevNodeHelper.GetGuidProperty(devInst, in DevNodeHelper.DEVPKEY_Device_ContainerId);
        string? parentId = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_Parent);
        string? driverVersionStr = DevNodeHelper.GetStringProperty(devInst, in DevNodeHelper.DEVPKEY_Device_DriverVersion);
        string[]? hardwareIds = DevNodeHelper.GetStringListProperty(devInst, in DevNodeHelper.DEVPKEY_Device_HardwareIds);
        string[]? compatibleIds = DevNodeHelper.GetStringListProperty(devInst, in DevNodeHelper.DEVPKEY_Device_CompatibleIds);
        string[]? locationPaths = DevNodeHelper.GetStringListProperty(devInst, in DevNodeHelper.DEVPKEY_Device_LocationPaths);

        // Suppress all-zero container IDs (means "no container")
        if (containerId == Guid.Empty) containerId = null;

        ParseUsbIds(instanceId, out HardwareId? vid, out HardwareId? pid);
        UsbClassCode? usbClassCode = ParseUsbClassCode(
            compatibleIds: compatibleIds,
            hardwareIds: hardwareIds,
            deviceId: instanceId,
            pnpDeviceId: instanceId);

        string? classGuidString = classGuid?.ToString("B");
        DeviceCategory category = WindowsCategoryMap.ResolveCategory(classGuidString);

        // Tier 2 — storage and ports enrichment (synchronous, no external calls)
        var driveType = category == DeviceCategory.Storage
            ? WindowsStorageEnricher.InferDriveType(devInst, classGuidString)
            : null;
        var portName = category == DeviceCategory.Ports
            ? WindowsPortsEnricher.GetPortName(instanceId)
            : null;

        var propertiesBuilder = ImmutableDictionary.CreateBuilder<string, object?>();
        if (hardwareIds is not null)
            propertiesBuilder["HardwareID"] = hardwareIds;
        if (compatibleIds is not null)
            propertiesBuilder["CompatibleID"] = compatibleIds;

        // Store the CM problem code as RawStatus (replaces WMI CIM status string).
        int? problemCode = DevNodeHelper.GetProblemCode(devInst);
        DeviceStatus status = ParseStatusFromProblemCode(problemCode);
        if (problemCode is not null)
            propertiesBuilder["RawStatus"] = problemCode.Value;

        return new DeviceInfo
        {
            Id = instanceId,
            Name = friendlyName ?? description,
            Category = category,
            Manufacturer = manufacturer,
            ClassGuid = classGuid,
            ClassName = classGuidString is not null && DeviceClassGuids.TryGetClassName(classGuidString, out var resolvedClassName)
                ? resolvedClassName
                : rawClassName,
            ContainerId = containerId,
            VendorId = vid,
            ProductId = pid,
            SerialNumber = ParseSerialNumber(instanceId),
            IsActive = DevNodeHelper.IsDeviceConnected(devInst),
            Status = status,
            BusType = WindowsCategoryMap.InferBusType(instanceId),
            LocationPath = ResolveLocationPath(instanceId, locationPaths, parentId, LookupNodeForLocation),
            Driver = service,
            DriverVersion = driverVersionStr is not null && Version.TryParse(driverVersionStr, out var ver) ? ver : null,
            ParentId = DeviceId.TryParse(parentId, out var parent) ? parent : (DeviceId?)null,
            PortNumber = (int?)DevNodeHelper.GetUInt32Property(devInst, in DevNodeHelper.DEVPKEY_Device_Address),
            DriveType = driveType,
            PortName = portName,
            UsbClassCode = usbClassCode,
            Properties = propertiesBuilder.ToImmutable(),
        };
    }

    /// <summary>
    /// Resolves the physical-port <see cref="DeviceInfo.LocationPath"/> for a device node, walking up
    /// the parent chain when the node's own <c>DEVPKEY_Device_LocationPaths</c> is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A USB device node carries its port as <c>DEVPKEY_Device_LocationPaths[0]</c>
    /// (e.g. <c>PCIROOT(20)#…#USB(6)#USB(3)</c>). But a <b>function/interface</b> node — notably the EFM8
    /// HID bootloader (<c>HID\VID_10C4&amp;PID_EAC9\…</c>) that a Treehopper re-enumerates as — has an
    /// <b>empty</b> <c>LocationPaths</c>, so the old code fell back to the instance id and topology
    /// correlation (<c>Periphery.Bootloader.DeviceCorrelationMode.ByLocationPath</c> — a cref cannot
    /// resolve it, the core does not reference the extension) never matched
    /// the app device's real port — it timed out on hardware. The port <em>is</em> recoverable: the HID
    /// node's parent is its USB node, whose <c>LocationPaths</c> is the real port and is identical across
    /// the app↔bootloader reset. So when a node exposes no port of its own, walk <c>DEVPKEY_Device_Parent</c>
    /// (bounded) until an ancestor does — one hop is enough for HID → USB, but the walk tolerates deeper
    /// function/interface layering. This lives in the shell (it does IO via <paramref name="lookupNode"/>);
    /// the walk itself is a pure fold over the chain so it is unit-testable without hardware.
    /// </para>
    /// <para>
    /// A no-op when the node already carries its own port (the common case — no extra cfgmgr calls), so
    /// the populated path is never regressed; falls back to <paramref name="instanceId"/> when no ancestor
    /// carries a port, preserving the prior behavior for a genuinely port-less device.
    /// </para>
    /// </remarks>
    /// <param name="instanceId">The node's own instance id — the last-resort fallback.</param>
    /// <param name="ownLocationPaths">The node's own <c>DEVPKEY_Device_LocationPaths</c> (may be null/empty).</param>
    /// <param name="parentId">The node's <c>DEVPKEY_Device_Parent</c> instance id, or null at the root.</param>
    /// <param name="lookupNode">
    /// Resolves an ancestor instance id to its <c>(LocationPaths, ParentId)</c>, or <c>null</c> if the node
    /// cannot be located. The shell backs this with cfgmgr32; tests supply a fake chain.
    /// </param>
    /// <param name="maxDepth">Cap on ancestors walked, so a broken/cyclic chain can never loop.</param>
    internal static string ResolveLocationPath(
        string instanceId,
        string[]? ownLocationPaths,
        string? parentId,
        Func<string, (string[]? LocationPaths, string? ParentId)?> lookupNode,
        int maxDepth = 8)
    {
        if (ownLocationPaths is { Length: > 0 } && !string.IsNullOrEmpty(ownLocationPaths[0]))
            return ownLocationPaths[0];

        string? currentParent = parentId;
        for (int depth = 0; depth < maxDepth && !string.IsNullOrEmpty(currentParent); depth++)
        {
            if (lookupNode(currentParent!) is not { } node)
                break;
            if (node.LocationPaths is { Length: > 0 } && !string.IsNullOrEmpty(node.LocationPaths[0]))
                return node.LocationPaths[0];
            currentParent = node.ParentId;
        }

        return instanceId;
    }

    // Shell backing for ResolveLocationPath's parent walk: locate the ancestor node and read the two
    // properties the walk needs. Returns null when the node cannot be located (a phantom/removed parent).
    private static (string[]? LocationPaths, string? ParentId)? LookupNodeForLocation(string instanceId)
    {
        int? devInst = DevNodeHelper.LocateDevNode(instanceId);
        if (devInst is null) return null;
        return (
            DevNodeHelper.GetStringListProperty(devInst.Value, in DevNodeHelper.DEVPKEY_Device_LocationPaths),
            DevNodeHelper.GetStringProperty(devInst.Value, in DevNodeHelper.DEVPKEY_Device_Parent));
    }

    /// <summary>
    /// Attempts to build a <see cref="DeviceInfo"/> from a device instance ID.
    /// Used by the monitor provider when a device arrives.
    /// Returns <c>null</c> if the device node cannot be located.
    /// </summary>
    internal static DeviceInfo? TryBuildDeviceInfo(string instanceId)
    {
        int? devInst = DevNodeHelper.LocateDevNode(instanceId);
        if (devInst is null) return null;
        DeviceInfo device = ToDeviceInfo(devInst.Value, instanceId);
        device = WindowsNetworkEnricher.Enrich(device); // Tier 2 network enrichment
        device = WindowsBatteryEnricher.Enrich(device, WindowsBatteryEnricher.TryReadSnapshot());
        // Tier 4: registered enrichers (ADR-0024 §3c). Called from the
        // monitor provider's [UnmanagedCallersOnly]-rooted arrival path
        // so use the sync overload — registered enrichers must complete
        // synchronously on this path or block. Current registry
        // (HidBatteryEnricher) is a dictionary-lookup-sync enricher
        // returning Task.FromResult, so the GetAwaiter().GetResult()
        // inside RunRegisteredSync doesn't actually block.
        device = EnrichmentPipeline.RunRegisteredSync(device, CancellationToken.None, _logger);
        return device;
    }

    // ── Status parsing ─────────────────────────────────────────────────

    private static DeviceStatus ParseStatusFromProblemCode(int? problemCode)
    {
        if (problemCode is null) return DeviceStatus.Unknown;

        return problemCode.Value switch
        {
            0                => DeviceStatus.OK,
            CM_PROB_DISABLED => DeviceStatus.Disabled,
            _                => DeviceStatus.Error,
        };
    }

    // ── USB / Hardware ID parsing (pure methods, no OS dependency) ─────

    internal static UsbClassCode? ParseUsbClassCode(
        object? compatibleIds,
        object? hardwareIds,
        string? deviceId,
        string? pnpDeviceId)
    {
        return SelectMostSpecificClassCode(new[]
        {
            ParseUsbClassCodeFromIdentifierSource(compatibleIds),
            ParseUsbClassCodeFromIdentifierSource(hardwareIds),
            ParseUsbClassCodeFromIdentifierSource(deviceId),
            ParseUsbClassCodeFromIdentifierSource(pnpDeviceId),
        });
    }

    private static UsbClassCode? SelectMostSpecificClassCode(IEnumerable<UsbClassCode?> candidates)
    {
        UsbClassCode? best = null;
        int bestScore = -1;

        foreach (var candidate in candidates)
        {
            if (candidate is not { } code)
                continue;

            int score = ClassSpecificityScore(code);
            if (score > bestScore)
            {
                best = code;
                bestScore = score;
            }
        }

        return best;
    }

    private static int ClassSpecificityScore(UsbClassCode code)
        => code.Subclass != 0x00 ? 2 : 1;

    internal static UsbClassCode? ParseUsbClassCodeFromCompatibleIds(object? compatibleIds)
        => ParseUsbClassCodeFromIdentifierSource(compatibleIds);

    private static UsbClassCode? ParseUsbClassCodeFromIdentifierSource(object? ids)
    {
        return ids switch
        {
            null => null,
            string id => TryParseUsbClassCodeFromCompatibleId(id, out var single) ? single : null,
            string[] values => ParseUsbClassCodeFromCompatibleIdList(values),
            Array values => ParseUsbClassCodeFromCompatibleIdList(ToStringValues(values)),
            _ => null,
        };
    }

    private static UsbClassCode? ParseUsbClassCodeFromCompatibleIdList(IEnumerable<string> ids)
    {
        UsbClassCode? best = null;
        int bestScore = -1;

        foreach (var id in ids)
        {
            if (!TryParseUsbClassCodeFromCompatibleId(id, out var code))
                continue;

            int score = ClassSpecificityScore(code);
            if (score > bestScore)
            {
                best = code;
                bestScore = score;
            }
        }

        return best;
    }

    private static IEnumerable<string> ToStringValues(Array values)
    {
        foreach (var value in values)
        {
            if (value?.ToString() is { Length: > 0 } s)
                yield return s;
        }
    }

    private static bool TryParseUsbClassCodeFromCompatibleId(string? compatibleId, out UsbClassCode code)
    {
        code = default;

        if (string.IsNullOrWhiteSpace(compatibleId))
            return false;

        var match = UsbClassCompatibleIdRegex().Match(compatibleId);
        if (!match.Success)
            return false;

        if (!byte.TryParse(match.Groups["class"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var @class))
            return false;

        byte subclass = 0x00;
        byte protocol = 0x00;

        if (match.Groups["subclass"].Success
            && !byte.TryParse(match.Groups["subclass"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out subclass))
        {
            return false;
        }

        if (match.Groups["protocol"].Success
            && !byte.TryParse(match.Groups["protocol"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out protocol))
        {
            return false;
        }

        code = new UsbClassCode(@class, subclass, protocol);
        return true;
    }

    private static void ParseUsbIds(string deviceId, out HardwareId? vid, out HardwareId? pid)
    {
        vid = null;
        pid = null;

        var match = VidPidRegex().Match(deviceId);
        if (match.Success)
        {
            if (HardwareId.TryParse(match.Groups["vid"].Value, out var parsedVid))
                vid = parsedVid;
            if (HardwareId.TryParse(match.Groups["pid"].Value, out var parsedPid))
                pid = parsedPid;
        }
    }

    private static string? ParseSerialNumber(string? pnpDeviceId)
    {
        if (pnpDeviceId is null) return null;

        // USB serial numbers appear after the last backslash in PNPDeviceID.
        int lastSlash = pnpDeviceId.LastIndexOf('\\');
        if (lastSlash < 0 || lastSlash >= pnpDeviceId.Length - 1) return null;

        string candidate = pnpDeviceId[(lastSlash + 1)..];

        // Filter out Windows-generated instance IDs (they contain '&').
        if (candidate.Contains('&')) return null;

        return candidate;
    }

    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})", RegexOptions.Compiled)]
    private static partial Regex VidPidRegex();

    [GeneratedRegex(@"Class_(?<class>[0-9A-Fa-f]{2})(?:&SubClass_(?<subclass>[0-9A-Fa-f]{2}))?(?:&Prot_(?<protocol>[0-9A-Fa-f]{2}))?", RegexOptions.Compiled)]
    private static partial Regex UsbClassCompatibleIdRegex();
}
