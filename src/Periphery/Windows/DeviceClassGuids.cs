// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery.Windows;

/// <summary>
/// Well-known device setup class GUIDs from the Windows SDK <c>devguid.h</c>.
/// Use <see cref="TryGetClassName"/> to resolve a ClassGuid returned by the
/// Windows device property store into a human-readable name.
/// </summary>
public static class DeviceClassGuids
{
    // ── Primary device classes ─────────────────────────────────────────

    public const string IEEE1394               = "{6bdd1fc1-810f-11d0-bec7-08002be2092f}";
    public const string IEEE1394Debug          = "{66f250d6-7801-4a64-b139-eea80a450b24}";
    public const string IEC61883               = "{7ebefbc0-3200-11d2-b4c2-00a0c9697d07}";
    public const string Adapter                = "{4d36e964-e325-11ce-bfc1-08002be10318}";
    public const string ApmSupport             = "{d45b1c18-c8fa-11d1-9f77-0000f805f530}";
    public const string Avc                    = "{c06ff265-ae09-48f0-812c-16753d7cba83}";
    public const string Battery                = "{72631e54-78a4-11d0-bcf7-00aa00b7b32a}";
    public const string Biometric              = "{53d29ef7-377c-4d14-864b-eb3a85769359}";
    public const string Bluetooth              = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    public const string Camera                 = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";
    public const string CdRom                  = "{4d36e965-e325-11ce-bfc1-08002be10318}";
    public const string ComputeAccelerator     = "{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";
    public const string Computer               = "{4d36e966-e325-11ce-bfc1-08002be10318}";
    public const string Decoder                = "{6bdd1fc2-810f-11d0-bec7-08002be2092f}";
    public const string DiskDrive              = "{4d36e967-e325-11ce-bfc1-08002be10318}";
    public const string Display                = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string Dot4                   = "{48721b56-6795-11d2-b1a8-0080c72e74a2}";
    public const string Dot4Print              = "{49ce6ac8-6f86-11d2-b1e5-0080c72e74a2}";
    public const string EhStorageSilo          = "{9da2b80f-f89f-4a49-a5c2-511b085b9e8a}";
    public const string Enum1394               = "{c459df55-db08-11d1-b009-00a0c9081ff6}";
    public const string Extension              = "{e2f84ce7-8efa-411c-aa69-97454ca4cb57}";
    public const string Fdc                    = "{4d36e969-e325-11ce-bfc1-08002be10318}";
    public const string Firmware               = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";
    public const string FloppyDisk             = "{4d36e980-e325-11ce-bfc1-08002be10318}";
    public const string Generic                = "{ff494df1-c4ed-4fac-9b3f-3786f6e91e7e}";
    public const string Gps                    = "{6bdd1fc3-810f-11d0-bec7-08002be2092f}";
    public const string Hdc                    = "{4d36e96a-e325-11ce-bfc1-08002be10318}";
    public const string HidClass               = "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}";
    public const string Holographic            = "{d612553d-06b1-49ca-8938-e39ef80eb16f}";
    public const string Image                  = "{6bdd1fc6-810f-11d0-bec7-08002be2092f}";
    public const string InfiniBand             = "{30ef7132-d858-4a0c-ac24-b9028a5cca3f}";
    public const string Infrared               = "{6bdd1fc5-810f-11d0-bec7-08002be2092f}";
    public const string Keyboard               = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    public const string LegacyDriver           = "{8ecc055d-047f-11d1-a537-0000f8753ed1}";
    public const string Media                  = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
    public const string MediumChanger          = "{ce5939ae-ebde-11d0-b181-0000f8753ec4}";
    public const string Memory                 = "{5099944a-f6b9-4057-a056-8c550228544c}";
    public const string Modem                  = "{4d36e96d-e325-11ce-bfc1-08002be10318}";
    public const string Monitor                = "{4d36e96e-e325-11ce-bfc1-08002be10318}";
    public const string Mouse                  = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
    public const string Mtd                    = "{4d36e970-e325-11ce-bfc1-08002be10318}";
    public const string Multifunction          = "{4d36e971-e325-11ce-bfc1-08002be10318}";
    public const string MultiportSerial        = "{50906cb8-ba12-11d1-bf5d-0000f805f530}";
    public const string Net                    = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string NetClient              = "{4d36e973-e325-11ce-bfc1-08002be10318}";
    public const string NetDriver              = "{87ef9ad1-8f70-49ee-b215-ab1fcadcbe3c}";
    public const string NetService             = "{4d36e974-e325-11ce-bfc1-08002be10318}";
    public const string NetTrans               = "{4d36e975-e325-11ce-bfc1-08002be10318}";
    public const string NetUio                 = "{78912bc1-cb8e-4b28-a329-f322ebadbe0f}";
    public const string NoDriver               = "{4d36e976-e325-11ce-bfc1-08002be10318}";
    public const string Pcmcia                 = "{4d36e977-e325-11ce-bfc1-08002be10318}";
    public const string PnpPrinters            = "{4658ee7e-f050-11d1-b6bd-00c04fa372a7}";
    public const string Ports                  = "{4d36e978-e325-11ce-bfc1-08002be10318}";
    public const string Primitive              = "{242681d1-eed3-41d2-a1ef-1468fc843106}";
    public const string Printer                = "{4d36e979-e325-11ce-bfc1-08002be10318}";
    public const string PrinterUpgrade         = "{4d36e97a-e325-11ce-bfc1-08002be10318}";
    public const string PrintQueue             = "{1ed2bbf9-11f0-4084-b21f-ad83a8e6dcdc}";
    public const string Processor              = "{50127dc3-0f36-415e-a6cc-4cb3be910b65}";
    public const string Sbp2                   = "{d48179be-ec20-11d1-b6b8-00c04fa372a7}";
    public const string ScmDisk                = "{53966cb1-4d46-4166-bf23-c522403cd495}";
    public const string ScmVolume              = "{53ccb149-e543-4c84-b6e0-bce4f6b7e806}";
    public const string ScsiAdapter            = "{4d36e97b-e325-11ce-bfc1-08002be10318}";
    public const string SecurityAccelerator    = "{268c95a1-edfe-11d3-95c3-0010dc4050a5}";
    public const string Sensor                 = "{5175d334-c371-4806-b3ba-71fd53c9258d}";
    public const string SideShow               = "{997b5d8d-c442-4f2e-baf3-9c8e671e9e21}";
    public const string SmartCardReader        = "{50dd5230-ba8a-11d1-bf5d-0000f805f530}";
    public const string SmrDisk                = "{53487c23-680f-4585-acc3-1f10d6777e82}";
    public const string SmrVolume              = "{53b3cf03-8f5a-4788-91b6-d19ed9fcccbf}";
    public const string SoftwareComponent      = "{5c4c3332-344d-483c-8739-259e934c9cc8}";
    public const string Sound                  = "{4d36e97c-e325-11ce-bfc1-08002be10318}";
    public const string System                 = "{4d36e97d-e325-11ce-bfc1-08002be10318}";
    public const string TapeDrive              = "{6d807884-7d21-11cf-801c-08002be10318}";
    public const string Unknown                = "{4d36e97e-e325-11ce-bfc1-08002be10318}";
    public const string Ucm                    = "{e6f1aa1c-7f3b-4473-b2e8-c97d8ac71d53}";
    public const string Usb                    = "{36fc9e60-c465-11cf-8056-444553540000}";
    public const string Volume                 = "{71a27cdd-812a-11d0-bec7-08002be2092f}";
    public const string VolumeSnapshot         = "{533c5b84-ec70-11d2-9505-00c04f79deaf}";
    public const string WceUsbs                = "{25dbce51-6c8f-4a72-8a6d-b54c2b4fc835}";
    public const string Wpd                    = "{eec5ad98-8080-425f-922a-dabf3de3f69a}";

    // ── Filesystem filter classes ──────────────────────────────────────

    public const string FsFilterTop                    = "{b369baf4-5568-4e82-a87e-a93eb16bca87}";
    public const string FsFilterActivityMonitor        = "{b86dff51-a31e-4bac-b3cf-e8cfe75c9fc2}";
    public const string FsFilterUndelete               = "{fe8f1572-c67a-48c0-bbac-0b5c6d66cafb}";
    public const string FsFilterAntivirus              = "{b1d1a169-c54f-4379-81db-bee7d88d7454}";
    public const string FsFilterReplication             = "{48d3ebc4-4cf8-48ff-b869-9c68ad42eb9f}";
    public const string FsFilterContinuousBackup        = "{71aa14f8-6fad-4622-ad77-92bb9d7e6947}";
    public const string FsFilterContentScreener         = "{3e3f0674-c83c-4558-bb26-9820e1eba5c5}";
    public const string FsFilterQuotaManagement         = "{8503c911-a6c7-4919-8f79-5028f5866b0c}";
    public const string FsFilterSystemRecovery          = "{2db15374-706e-4131-a0c7-d7c78eb0289a}";
    public const string FsFilterCfsMetadataServer       = "{cdcf0939-b75b-4630-bf76-80f7ba655884}";
    public const string FsFilterHsm                     = "{d546500a-2aeb-45f6-9482-f4b1799c3177}";
    public const string FsFilterCompression             = "{f3586baf-b5aa-49b5-8d6c-0569284c639f}";
    public const string FsFilterEncryption              = "{a0a701c0-a511-42ff-aa6c-06dc0395576f}";
    public const string FsFilterVirtualization          = "{f75a86c0-10d8-4c3a-b233-ed60e4cdfaac}";
    public const string FsFilterPhysicalQuotaManagement = "{6a0a8e78-bba6-4fc4-a709-1e33cd09d67e}";
    public const string FsFilterOpenFileBackup          = "{f8ecafa6-66d1-41a5-899b-66585d7216b7}";
    public const string FsFilterSecurityEnhancer        = "{d02bc3da-0c8e-4945-9bd5-f1883c226c8c}";
    public const string FsFilterCopyProtection          = "{89786ff1-9c12-402f-9c9e-17753c7f4375}";
    public const string FsFilterBottom                  = "{37765ea0-5958-4fc9-b04b-2fdfef97e59e}";
    public const string FsFilterSystem                  = "{5d1b9aaa-01e2-46af-849f-272b3f324c46}";
    public const string FsFilterInfrastructure          = "{e55fa6f9-128c-4d04-abab-630c74b1453a}";

    // ── Lookup dictionary ──────────────────────────────────────────────

    /// <summary>
    /// Maps a device class GUID string (with braces, case-insensitive) to
    /// a human-readable class name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> All =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [IEEE1394]               = "IEEE 1394",
            [IEEE1394Debug]          = "IEEE 1394 Debug",
            [IEC61883]               = "IEC 61883",
            [Adapter]                = "Adapter",
            [ApmSupport]             = "APM Support",
            [Avc]                    = "AVC",
            [Battery]                = "Battery",
            [Biometric]              = "Biometric",
            [Bluetooth]              = "Bluetooth",
            [Camera]                 = "Camera",
            [CdRom]                  = "CD-ROM",
            [ComputeAccelerator]     = "Compute Accelerator",
            [Computer]               = "Computer",
            [Decoder]                = "Decoder",
            [DiskDrive]              = "Disk Drive",
            [Display]                = "Display",
            [Dot4]                   = "IEEE 1284.4 (Dot4)",
            [Dot4Print]              = "IEEE 1284.4 Print",
            [EhStorageSilo]          = "Enhanced Storage Silo",
            [Enum1394]               = "IEEE 1394 Enumerator",
            [Extension]              = "Extension",
            [Fdc]                    = "Floppy Disk Controller",
            [Firmware]               = "Firmware",
            [FloppyDisk]             = "Floppy Disk",
            [Generic]                = "Generic",
            [Gps]                    = "GPS",
            [Hdc]                    = "Hard Disk Controller",
            [HidClass]               = "HID (Human Interface Device)",
            [Holographic]            = "Holographic",
            [Image]                  = "Imaging Device",
            [InfiniBand]             = "InfiniBand",
            [Infrared]               = "Infrared",
            [Keyboard]               = "Keyboard",
            [LegacyDriver]           = "Legacy Driver",
            [Media]                  = "Media",
            [MediumChanger]          = "Medium Changer",
            [Memory]                 = "Memory",
            [Modem]                  = "Modem",
            [Monitor]                = "Monitor",
            [Mouse]                  = "Mouse",
            [Mtd]                    = "Memory Technology Driver",
            [Multifunction]          = "Multifunction",
            [MultiportSerial]        = "Multiport Serial",
            [Net]                    = "Network Adapter",
            [NetClient]              = "Network Client",
            [NetDriver]              = "Network Driver",
            [NetService]             = "Network Service",
            [NetTrans]               = "Network Transport",
            [NetUio]                 = "Network UIO",
            [NoDriver]               = "No Driver",
            [Pcmcia]                 = "PCMCIA",
            [PnpPrinters]            = "PnP Printers",
            [Ports]                  = "Ports (COM & LPT)",
            [Primitive]              = "Primitive",
            [Printer]                = "Printer",
            [PrinterUpgrade]         = "Printer Upgrade",
            [PrintQueue]             = "Print Queue",
            [Processor]              = "Processor",
            [Sbp2]                   = "SBP-2 (IEEE 1394)",
            [ScmDisk]                = "SCM Disk",
            [ScmVolume]              = "SCM Volume",
            [ScsiAdapter]            = "SCSI / Storage Controller",
            [SecurityAccelerator]    = "Security Accelerator",
            [Sensor]                 = "Sensor",
            [SideShow]               = "SideShow",
            [SmartCardReader]        = "Smart Card Reader",
            [SmrDisk]                = "SMR Disk",
            [SmrVolume]              = "SMR Volume",
            [SoftwareComponent]      = "Software Component",
            [Sound]                  = "Sound",
            [System]                 = "System",
            [TapeDrive]              = "Tape Drive",
            [Unknown]                = "Unknown",
            [Ucm]                    = "USB Connector Manager",
            [Usb]                    = "USB",
            [Volume]                 = "Volume",
            [VolumeSnapshot]         = "Volume Snapshot",
            [WceUsbs]                = "Windows CE USB",
            [Wpd]                    = "Windows Portable Device",

            // Filesystem filter classes
            [FsFilterTop]                    = "FS Filter – Top",
            [FsFilterActivityMonitor]        = "FS Filter – Activity Monitor",
            [FsFilterUndelete]               = "FS Filter – Undelete",
            [FsFilterAntivirus]              = "FS Filter – Antivirus",
            [FsFilterReplication]             = "FS Filter – Replication",
            [FsFilterContinuousBackup]        = "FS Filter – Continuous Backup",
            [FsFilterContentScreener]         = "FS Filter – Content Screener",
            [FsFilterQuotaManagement]         = "FS Filter – Quota Management",
            [FsFilterSystemRecovery]          = "FS Filter – System Recovery",
            [FsFilterCfsMetadataServer]       = "FS Filter – CFS Metadata Server",
            [FsFilterHsm]                     = "FS Filter – HSM",
            [FsFilterCompression]             = "FS Filter – Compression",
            [FsFilterEncryption]              = "FS Filter – Encryption",
            [FsFilterVirtualization]          = "FS Filter – Virtualization",
            [FsFilterPhysicalQuotaManagement] = "FS Filter – Physical Quota Management",
            [FsFilterOpenFileBackup]          = "FS Filter – Open File Backup",
            [FsFilterSecurityEnhancer]        = "FS Filter – Security Enhancer",
            [FsFilterCopyProtection]          = "FS Filter – Copy Protection",
            [FsFilterBottom]                  = "FS Filter – Bottom",
            [FsFilterSystem]                  = "FS Filter – System",
            [FsFilterInfrastructure]          = "FS Filter – Infrastructure",
        };

    /// <summary>
    /// Resolves a class GUID string to a friendly name.
    /// Returns <c>true</c> and the name if found; otherwise <c>false</c>.
    /// </summary>
    public static bool TryGetClassName(string classGuid, out string className)
        => All.TryGetValue(classGuid, out className!);

    /// <summary>
    /// Returns the friendly name for a class GUID, or the raw GUID string
    /// if not found in the known list.
    /// </summary>
    public static string GetClassNameOrDefault(string? classGuid)
        => classGuid is not null && All.TryGetValue(classGuid, out string? name)
            ? name
            : classGuid ?? "(none)";
}
