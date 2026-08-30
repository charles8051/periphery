// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.IO;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Infers <see cref="DeviceInfo.DriveType"/> for Storage-category devices using
/// the device class GUID and the <c>CM_DEVCAP_REMOVABLE</c> capability flag
/// from <c>DEVPKEY_Device_Capabilities</c> (devpkey.h pid=17).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsStorageEnricher
{
    /// <summary>Device capability flag — device is removable from its bus.</summary>
    private const uint CM_DEVCAP_REMOVABLE = 0x00000004;

    /// <summary>
    /// Returns the inferred <see cref="System.IO.DriveType"/> for the device node,
    /// or <see langword="null"/> if the device is not a recognised storage class.
    /// </summary>
    internal static DriveType? InferDriveType(int devInst, string? classGuidString)
    {
        if (classGuidString is null)
            return null;

        // CD-ROM drives are always CDRom regardless of capabilities
        if (string.Equals(classGuidString, DeviceClassGuids.CdRom, StringComparison.OrdinalIgnoreCase))
            return DriveType.CDRom;

        // Floppy disks are always Removable
        if (string.Equals(classGuidString, DeviceClassGuids.FloppyDisk, StringComparison.OrdinalIgnoreCase))
            return DriveType.Removable;

        // Disk drives: read the CM_DEVCAP_REMOVABLE capability bit to distinguish
        // fixed internal drives from removable media (USB sticks, SD cards, etc.)
        if (string.Equals(classGuidString, DeviceClassGuids.DiskDrive, StringComparison.OrdinalIgnoreCase))
        {
            uint? caps = DevNodeHelper.GetUInt32Property(devInst, in DevNodeHelper.DEVPKEY_Device_Capabilities);
            if (caps is null)
                return null;
            return (caps.Value & CM_DEVCAP_REMOVABLE) != 0 ? DriveType.Removable : DriveType.Fixed;
        }

        return null;
    }
}
