// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// USB class/subclass/protocol triple identifying a device's function.
/// Follows the USB-IF Defined Class Codes specification.
/// </summary>
/// <remarks>
/// <para>Well-known base class constants are exposed as <c>static readonly</c>
/// fields (e.g. <see cref="Hid"/>, <see cref="MassStorage"/>). These use
/// subclass <c>0x00</c> and protocol <c>0x00</c>.</para>
/// <para>For full (class, subclass, protocol) triples, use nested static
/// classes (e.g. <see cref="AudioClass.Control"/>, <see cref="HidClass.BootKeyboard"/>).</para>
/// <para>Use <see cref="IsClass"/> to match on the base class regardless
/// of subclass/protocol.</para>
/// </remarks>
[JsonConverter(typeof(UsbClassCodeJsonConverter))]
public readonly struct UsbClassCode : IEquatable<UsbClassCode>, IFormattable
{
    /// <summary>USB base class code.</summary>
    public byte Class { get; }

    /// <summary>USB subclass code.</summary>
    public byte Subclass { get; }

    /// <summary>USB protocol code.</summary>
    public byte Protocol { get; }

    public UsbClassCode(byte @class, byte subclass, byte protocol)
    {
        Class = @class;
        Subclass = subclass;
        Protocol = protocol;
    }

    // ── Well-known base classes (USB-IF Defined Class Codes) ───────────
    // These match on the base class byte only (subclass/protocol = 0x00).
    // Use IsClass() for class-level matching regardless of subclass/protocol.

    /// <summary>Class 0x00 — Device class is unspecified; use interface descriptors.</summary>
    public static readonly UsbClassCode UseInterfaceDescriptor = new(0x00, 0x00, 0x00);

    /// <summary>Class 0x01 — Audio.</summary>
    public static readonly UsbClassCode Audio = new(0x01, 0x00, 0x00);

    /// <summary>Class 0x02 — Communications and CDC Control.</summary>
    public static readonly UsbClassCode CdcControl = new(0x02, 0x00, 0x00);

    /// <summary>Class 0x03 — Human Interface Device.</summary>
    public static readonly UsbClassCode Hid = new(0x03, 0x00, 0x00);

    /// <summary>Class 0x05 — Physical.</summary>
    public static readonly UsbClassCode Physical = new(0x05, 0x00, 0x00);

    /// <summary>Class 0x06 — Image (Still Imaging).</summary>
    public static readonly UsbClassCode Image = new(0x06, 0x00, 0x00);

    /// <summary>Class 0x07 — Printer.</summary>
    public static readonly UsbClassCode Printer = new(0x07, 0x00, 0x00);

    /// <summary>Class 0x08 — Mass Storage.</summary>
    public static readonly UsbClassCode MassStorage = new(0x08, 0x00, 0x00);

    /// <summary>Class 0x09 — Hub.</summary>
    public static readonly UsbClassCode Hub = new(0x09, 0x00, 0x00);

    /// <summary>Class 0x0A — CDC-Data.</summary>
    public static readonly UsbClassCode CdcData = new(0x0A, 0x00, 0x00);

    /// <summary>Class 0x0B — Smart Card.</summary>
    public static readonly UsbClassCode SmartCard = new(0x0B, 0x00, 0x00);

    /// <summary>Class 0x0D — Content Security.</summary>
    public static readonly UsbClassCode ContentSecurity = new(0x0D, 0x00, 0x00);

    /// <summary>Class 0x0E — Video.</summary>
    public static readonly UsbClassCode Video = new(0x0E, 0x00, 0x00);

    /// <summary>Class 0x0F — Personal Healthcare.</summary>
    public static readonly UsbClassCode PersonalHealthcare = new(0x0F, 0x00, 0x00);

    /// <summary>Class 0x10 — Audio/Video Devices.</summary>
    public static readonly UsbClassCode AudioVideo = new(0x10, 0x00, 0x00);

    /// <summary>Class 0x11 — Billboard Device.</summary>
    public static readonly UsbClassCode Billboard = new(0x11, 0x00, 0x00);

    /// <summary>Class 0x12 — USB Type-C Bridge.</summary>
    public static readonly UsbClassCode TypeCBridge = new(0x12, 0x00, 0x00);

    /// <summary>Class 0x13 — Bulk Display Protocol.</summary>
    public static readonly UsbClassCode BulkDisplay = new(0x13, 0x00, 0x00);

    /// <summary>Class 0x14 — MCTP over USB.</summary>
    public static readonly UsbClassCode Mctp = new(0x14, 0x00, 0x00);

    /// <summary>Class 0x3C — I3C Device Class.</summary>
    public static readonly UsbClassCode I3C = new(0x3C, 0x00, 0x00);

    /// <summary>Class 0xDC — Diagnostic Device.</summary>
    public static readonly UsbClassCode Diagnostic = new(0xDC, 0x00, 0x00);

    /// <summary>Class 0xE0 — Wireless Controller.</summary>
    public static readonly UsbClassCode WirelessController = new(0xE0, 0x00, 0x00);

    /// <summary>Class 0xEF — Miscellaneous.</summary>
    public static readonly UsbClassCode Miscellaneous = new(0xEF, 0x00, 0x00);

    /// <summary>Class 0xFE — Application Specific.</summary>
    public static readonly UsbClassCode ApplicationSpecific = new(0xFE, 0x00, 0x00);

    /// <summary>Class 0xFF — Vendor Specific.</summary>
    public static readonly UsbClassCode VendorSpecific = new(0xFF, 0x00, 0x00);

    // ── Subclass/protocol triples ──────────────────────────────────────

    /// <summary>Audio class (0x01) subclass/protocol triples.</summary>
    public static class AudioClass
    {
        /// <summary>Audio Control (0x01/0x01/0x00).</summary>
        public static readonly UsbClassCode Control = new(0x01, 0x01, 0x00);
        /// <summary>Audio Streaming (0x01/0x02/0x00).</summary>
        public static readonly UsbClassCode Streaming = new(0x01, 0x02, 0x00);
        /// <summary>MIDI Streaming (0x01/0x03/0x00).</summary>
        public static readonly UsbClassCode MidiStreaming = new(0x01, 0x03, 0x00);
    }

    /// <summary>CDC Control class (0x02) subclass/protocol triples.</summary>
    public static class CdcControlClass
    {
        /// <summary>Direct Line Control Model (0x02/0x01/0x00).</summary>
        public static readonly UsbClassCode DirectLine = new(0x02, 0x01, 0x00);
        /// <summary>Abstract Control Model (0x02/0x02/0x00) — common for virtual COM ports.</summary>
        public static readonly UsbClassCode AbstractControl = new(0x02, 0x02, 0x00);
        /// <summary>Abstract Control Model with AT commands V.25ter (0x02/0x02/0x01).</summary>
        public static readonly UsbClassCode AbstractControlAtCommands = new(0x02, 0x02, 0x01);
        /// <summary>Telephone Control Model (0x02/0x03/0x00).</summary>
        public static readonly UsbClassCode Telephone = new(0x02, 0x03, 0x00);
        /// <summary>Multi-Channel Control Model (0x02/0x04/0x00).</summary>
        public static readonly UsbClassCode MultiChannel = new(0x02, 0x04, 0x00);
        /// <summary>CAPI Control Model (0x02/0x05/0x00).</summary>
        public static readonly UsbClassCode Capi = new(0x02, 0x05, 0x00);
        /// <summary>Ethernet Networking Control Model (0x02/0x06/0x00).</summary>
        public static readonly UsbClassCode EthernetNetworking = new(0x02, 0x06, 0x00);
        /// <summary>ATM Networking Control Model (0x02/0x07/0x00).</summary>
        public static readonly UsbClassCode AtmNetworking = new(0x02, 0x07, 0x00);
        /// <summary>Wireless Handset Control Model (0x02/0x08/0x00).</summary>
        public static readonly UsbClassCode WirelessHandset = new(0x02, 0x08, 0x00);
        /// <summary>Device Management (0x02/0x09/0x00).</summary>
        public static readonly UsbClassCode DeviceManagement = new(0x02, 0x09, 0x00);
        /// <summary>Mobile Direct Line Model (0x02/0x0A/0x00).</summary>
        public static readonly UsbClassCode MobileDirectLine = new(0x02, 0x0A, 0x00);
        /// <summary>OBEX (0x02/0x0B/0x00).</summary>
        public static readonly UsbClassCode Obex = new(0x02, 0x0B, 0x00);
        /// <summary>Ethernet Emulation Model (0x02/0x0C/0x00).</summary>
        public static readonly UsbClassCode EthernetEmulation = new(0x02, 0x0C, 0x00);
        /// <summary>Network Control Model (0x02/0x0D/0x00).</summary>
        public static readonly UsbClassCode NetworkControl = new(0x02, 0x0D, 0x00);
    }

    /// <summary>HID class (0x03) subclass/protocol triples.</summary>
    public static class HidClass
    {
        /// <summary>No subclass (0x03/0x00/0x00).</summary>
        public static readonly UsbClassCode NoSubclass = new(0x03, 0x00, 0x00);
        /// <summary>Boot Interface, no protocol (0x03/0x01/0x00).</summary>
        public static readonly UsbClassCode BootInterface = new(0x03, 0x01, 0x00);
        /// <summary>Boot Keyboard (0x03/0x01/0x01).</summary>
        public static readonly UsbClassCode BootKeyboard = new(0x03, 0x01, 0x01);
        /// <summary>Boot Mouse (0x03/0x01/0x02).</summary>
        public static readonly UsbClassCode BootMouse = new(0x03, 0x01, 0x02);
    }

    /// <summary>Image class (0x06) subclass/protocol triples.</summary>
    public static class ImageClass
    {
        /// <summary>Still Image Capture — PTP (0x06/0x01/0x01).</summary>
        public static readonly UsbClassCode StillImagePtp = new(0x06, 0x01, 0x01);
    }

    /// <summary>Printer class (0x07) subclass/protocol triples.</summary>
    public static class PrinterClass
    {
        /// <summary>Unidirectional (0x07/0x01/0x01).</summary>
        public static readonly UsbClassCode Unidirectional = new(0x07, 0x01, 0x01);
        /// <summary>Bidirectional (0x07/0x01/0x02).</summary>
        public static readonly UsbClassCode Bidirectional = new(0x07, 0x01, 0x02);
        /// <summary>IEEE 1284.4 compatible (0x07/0x01/0x03).</summary>
        public static readonly UsbClassCode Ieee1284 = new(0x07, 0x01, 0x03);
        /// <summary>IPP over USB (0x07/0x01/0x04).</summary>
        public static readonly UsbClassCode IppOverUsb = new(0x07, 0x01, 0x04);
    }

    /// <summary>Mass Storage class (0x08) subclass/protocol triples.</summary>
    public static class MassStorageClass
    {
        /// <summary>SCSI command set not reported (0x08/0x00/0x00).</summary>
        public static readonly UsbClassCode ScsiNotReported = new(0x08, 0x00, 0x00);
        /// <summary>RBC (Reduced Block Commands) (0x08/0x01/0x00).</summary>
        public static readonly UsbClassCode Rbc = new(0x08, 0x01, 0x00);
        /// <summary>MMC-5 (ATAPI) (0x08/0x02/0x00).</summary>
        public static readonly UsbClassCode Atapi = new(0x08, 0x02, 0x00);
        /// <summary>UFI (USB Floppy Interface) (0x08/0x04/0x00).</summary>
        public static readonly UsbClassCode Ufi = new(0x08, 0x04, 0x00);
        /// <summary>SCSI transparent command set (0x08/0x06/0x00).</summary>
        public static readonly UsbClassCode ScsiTransparent = new(0x08, 0x06, 0x00);
        /// <summary>SCSI transparent, Control/Bulk/Interrupt (0x08/0x06/0x00).</summary>
        public static readonly UsbClassCode ScsiCbi = new(0x08, 0x06, 0x00);
        /// <summary>SCSI transparent, Bulk-Only Transport (0x08/0x06/0x50).</summary>
        public static readonly UsbClassCode ScsiBot = new(0x08, 0x06, 0x50);
        /// <summary>UAS (USB Attached SCSI) (0x08/0x06/0x62).</summary>
        public static readonly UsbClassCode Uas = new(0x08, 0x06, 0x62);
    }

    /// <summary>Hub class (0x09) subclass/protocol triples.</summary>
    public static class HubClass
    {
        /// <summary>Full-speed hub (0x09/0x00/0x00).</summary>
        public static readonly UsbClassCode FullSpeed = new(0x09, 0x00, 0x00);
        /// <summary>Hi-speed hub with single TT (0x09/0x00/0x01).</summary>
        public static readonly UsbClassCode HiSpeedSingleTt = new(0x09, 0x00, 0x01);
        /// <summary>Hi-speed hub with multiple TTs (0x09/0x00/0x02).</summary>
        public static readonly UsbClassCode HiSpeedMultipleTt = new(0x09, 0x00, 0x02);
        /// <summary>SuperSpeed hub (0x09/0x00/0x03).</summary>
        public static readonly UsbClassCode SuperSpeed = new(0x09, 0x00, 0x03);
    }

    /// <summary>Video class (0x0E) subclass/protocol triples.</summary>
    public static class VideoClass
    {
        /// <summary>Video Control (0x0E/0x01/0x00).</summary>
        public static readonly UsbClassCode Control = new(0x0E, 0x01, 0x00);
        /// <summary>Video Streaming (0x0E/0x02/0x00).</summary>
        public static readonly UsbClassCode Streaming = new(0x0E, 0x02, 0x00);
        /// <summary>Video Interface Collection (0x0E/0x03/0x00).</summary>
        public static readonly UsbClassCode InterfaceCollection = new(0x0E, 0x03, 0x00);
    }

    /// <summary>Audio/Video class (0x10) subclass/protocol triples.</summary>
    public static class AudioVideoClass
    {
        /// <summary>AV Control Interface (0x10/0x01/0x00).</summary>
        public static readonly UsbClassCode Control = new(0x10, 0x01, 0x00);
        /// <summary>AV Data Video Streaming (0x10/0x02/0x00).</summary>
        public static readonly UsbClassCode VideoStreaming = new(0x10, 0x02, 0x00);
        /// <summary>AV Data Audio Streaming (0x10/0x03/0x00).</summary>
        public static readonly UsbClassCode AudioStreaming = new(0x10, 0x03, 0x00);
    }

    /// <summary>Diagnostic Device class (0xDC) subclass/protocol triples.</summary>
    public static class DiagnosticClass
    {
        /// <summary>USB2 Compliance Device (0xDC/0x01/0x01).</summary>
        public static readonly UsbClassCode Usb2Compliance = new(0xDC, 0x01, 0x01);
        /// <summary>Debug Target (0xDC/0x02/0x00).</summary>
        public static readonly UsbClassCode DebugTarget = new(0xDC, 0x02, 0x00);
        /// <summary>Debug — GNU remote debug command (0xDC/0x02/0x01).</summary>
        public static readonly UsbClassCode DebugGnu = new(0xDC, 0x02, 0x01);
    }

    /// <summary>Wireless Controller class (0xE0) subclass/protocol triples.</summary>
    public static class WirelessControllerClass
    {
        /// <summary>Bluetooth Programming Interface (0xE0/0x01/0x01).</summary>
        public static readonly UsbClassCode BluetoothProgramming = new(0xE0, 0x01, 0x01);
        /// <summary>UWB Radio Control Interface (0xE0/0x01/0x02).</summary>
        public static readonly UsbClassCode UwbRadioControl = new(0xE0, 0x01, 0x02);
        /// <summary>Remote NDIS (0xE0/0x01/0x03).</summary>
        public static readonly UsbClassCode RemoteNdis = new(0xE0, 0x01, 0x03);
        /// <summary>Bluetooth AMP Controller (0xE0/0x01/0x04).</summary>
        public static readonly UsbClassCode BluetoothAmp = new(0xE0, 0x01, 0x04);
        /// <summary>Host Wire Adapter Control/Data (0xE0/0x02/0x01).</summary>
        public static readonly UsbClassCode HostWireAdapter = new(0xE0, 0x02, 0x01);
        /// <summary>Device Wire Adapter Control/Data (0xE0/0x02/0x02).</summary>
        public static readonly UsbClassCode DeviceWireAdapter = new(0xE0, 0x02, 0x02);
        /// <summary>Device Wire Adapter Isochronous (0xE0/0x02/0x03).</summary>
        public static readonly UsbClassCode DeviceWireAdapterIsoc = new(0xE0, 0x02, 0x03);
    }

    /// <summary>Miscellaneous class (0xEF) subclass/protocol triples.</summary>
    public static class MiscellaneousClass
    {
        /// <summary>ActiveSync device (0xEF/0x01/0x01).</summary>
        public static readonly UsbClassCode ActiveSync = new(0xEF, 0x01, 0x01);
        /// <summary>Palm Sync (0xEF/0x01/0x02).</summary>
        public static readonly UsbClassCode PalmSync = new(0xEF, 0x01, 0x02);
        /// <summary>Interface Association Descriptor (0xEF/0x02/0x01).</summary>
        public static readonly UsbClassCode InterfaceAssociation = new(0xEF, 0x02, 0x01);
        /// <summary>Wire Adapter Multifunction Peripheral (0xEF/0x02/0x02).</summary>
        public static readonly UsbClassCode WireAdapterMultifunction = new(0xEF, 0x02, 0x02);
        /// <summary>Cable Based Association Framework (0xEF/0x03/0x01).</summary>
        public static readonly UsbClassCode CableBasedAssociation = new(0xEF, 0x03, 0x01);
        /// <summary>RNDIS over Ethernet (0xEF/0x04/0x01).</summary>
        public static readonly UsbClassCode RndisEthernet = new(0xEF, 0x04, 0x01);
        /// <summary>RNDIS over WiFi (0xEF/0x04/0x02).</summary>
        public static readonly UsbClassCode RndisWifi = new(0xEF, 0x04, 0x02);
        /// <summary>RNDIS over WiMAX (0xEF/0x04/0x03).</summary>
        public static readonly UsbClassCode RndisWimax = new(0xEF, 0x04, 0x03);
        /// <summary>RNDIS over WWAN (0xEF/0x04/0x04).</summary>
        public static readonly UsbClassCode RndisWwan = new(0xEF, 0x04, 0x04);
        /// <summary>RNDIS for Raw IPv4 (0xEF/0x04/0x05).</summary>
        public static readonly UsbClassCode RndisIpv4 = new(0xEF, 0x04, 0x05);
        /// <summary>RNDIS for Raw IPv6 (0xEF/0x04/0x06).</summary>
        public static readonly UsbClassCode RndisIpv6 = new(0xEF, 0x04, 0x06);
        /// <summary>RNDIS for GPRS (0xEF/0x04/0x07).</summary>
        public static readonly UsbClassCode RndisGprs = new(0xEF, 0x04, 0x07);
        /// <summary>USB3 Vision Control (0xEF/0x05/0x00).</summary>
        public static readonly UsbClassCode Usb3VisionControl = new(0xEF, 0x05, 0x00);
        /// <summary>USB3 Vision Event (0xEF/0x05/0x01).</summary>
        public static readonly UsbClassCode Usb3VisionEvent = new(0xEF, 0x05, 0x01);
        /// <summary>USB3 Vision Streaming (0xEF/0x05/0x02).</summary>
        public static readonly UsbClassCode Usb3VisionStreaming = new(0xEF, 0x05, 0x02);
        /// <summary>STEP — Stream Transport Efficient Protocol (0xEF/0x06/0x01).</summary>
        public static readonly UsbClassCode Step = new(0xEF, 0x06, 0x01);
        /// <summary>STEP RAW — Stream Transport Efficient Protocol (0xEF/0x06/0x02).</summary>
        public static readonly UsbClassCode StepRaw = new(0xEF, 0x06, 0x02);
        /// <summary>Command Verifier Interface (0xEF/0x07/0x00).</summary>
        public static readonly UsbClassCode CommandVerifier = new(0xEF, 0x07, 0x00);
    }

    /// <summary>Application Specific class (0xFE) subclass/protocol triples.</summary>
    public static class ApplicationSpecificClass
    {
        /// <summary>Device Firmware Upgrade (0xFE/0x01/0x01).</summary>
        public static readonly UsbClassCode DeviceFirmwareUpgrade = new(0xFE, 0x01, 0x01);
        /// <summary>IrDA Bridge (0xFE/0x02/0x00).</summary>
        public static readonly UsbClassCode IrdaBridge = new(0xFE, 0x02, 0x00);
        /// <summary>USB Test and Measurement Class (0xFE/0x03/0x00).</summary>
        public static readonly UsbClassCode TestAndMeasurement = new(0xFE, 0x03, 0x00);
        /// <summary>USB Test and Measurement Class, USBTMC USB488 (0xFE/0x03/0x01).</summary>
        public static readonly UsbClassCode TestAndMeasurementUsb488 = new(0xFE, 0x03, 0x01);
    }

    // ── Matching helpers ───────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if this code's <see cref="Class"/> byte matches
    /// <paramref name="classCode"/>, regardless of subclass and protocol.
    /// </summary>
    public bool IsClass(byte classCode) => Class == classCode;

    /// <summary>
    /// Returns <c>true</c> if this code's <see cref="Class"/> and
    /// <see cref="Subclass"/> bytes match, regardless of protocol.
    /// </summary>
    public bool IsClassAndSubclass(byte classCode, byte subclass)
        => Class == classCode && Subclass == subclass;

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a class code from a hex string in the format <c>"CC/SS/PP"</c>
    /// or <c>"CC:SS:PP"</c> (e.g. <c>"03/01/01"</c> for boot keyboard).
    /// </summary>
    public static UsbClassCode Parse(string s)
        => TryParse(s, out var result)
            ? result
            : throw new FormatException($"'{s}' is not a valid USB class code. Expected format: CC/SS/PP or CC:SS:PP.");

    public static bool TryParse(string? s, [MaybeNullWhen(false)] out UsbClassCode result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var separators = new[] { '/', ':' };
        var parts = s.Split(separators);
        if (parts.Length != 3) return false;

        if (!byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte cls)) return false;
        if (!byte.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte sub)) return false;
        if (!byte.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte proto)) return false;

        result = new UsbClassCode(cls, sub, proto);
        return true;
    }

    // ── Formatting ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the class code in <c>"CC/SS/PP"</c> hex format
    /// (e.g. <c>"03/01/01"</c> for boot keyboard).
    /// </summary>
    public override string ToString()
        => $"{Class:X2}/{Subclass:X2}/{Protocol:X2}";

    public string ToString(string? format, IFormatProvider? formatProvider)
        => ToString();

    // ── Equality ───────────────────────────────────────────────────────

    public bool Equals(UsbClassCode other)
        => Class == other.Class && Subclass == other.Subclass && Protocol == other.Protocol;

    public override bool Equals(object? obj) => obj is UsbClassCode other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Class, Subclass, Protocol);

    public static bool operator ==(UsbClassCode left, UsbClassCode right) => left.Equals(right);
    public static bool operator !=(UsbClassCode left, UsbClassCode right) => !left.Equals(right);
}
