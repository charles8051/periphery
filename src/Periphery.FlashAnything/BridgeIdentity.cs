// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Diagnostics.CodeAnalysis;

namespace Periphery.FlashAnything;

/// <summary>
/// The stable identity of a USB-serial bridge an operator bound autoflash to.
/// <para>
/// A probe-identified target has two levels of identity and only the outer one is real. The bridge
/// is a genuine device — VID/PID, a physical port, often a serial number. What sits behind it is
/// knowable only by probing, and a probe returns a family: every STM32G431 answers Get ID with
/// <c>0x0468</c>, so two boards in sequence are indistinguishable. Autoflash therefore binds to the
/// bridge (adr.md Decision 8) and treats the chip as an occupancy state of it.
/// </para>
/// <para>
/// Not a COM name. Windows recycles those: unplug the bound bridge, plug in a GPS receiver, and the
/// OS can hand it <c>COM7</c> — a loop authorised against the string would keep probing and now
/// send AN3155 bytes to the GPS. The operator consented to a bench, not to a number, and the number
/// is the part that moves.
/// </para>
/// </summary>
public readonly record struct BridgeIdentity
{
    private BridgeIdentity(HardwareId vendorId, HardwareId productId, string? serialNumber, string? locationPath)
    {
        VendorId = vendorId;
        ProductId = productId;
        SerialNumber = serialNumber;
        LocationPath = locationPath;
    }

    /// <summary>The bridge's USB vendor id.</summary>
    public HardwareId VendorId { get; }

    /// <summary>The bridge's USB product id.</summary>
    public HardwareId ProductId { get; }

    /// <summary>The bridge's serial number, when it exposes one. CH340s commonly do not.</summary>
    public string? SerialNumber { get; }

    /// <summary>The physical port the bridge is plugged into, when the platform reports one.</summary>
    public string? LocationPath { get; }

    /// <summary>
    /// Builds the identity of the bridge behind <paramref name="device"/>, or reports why it cannot
    /// be bound.
    /// <para>
    /// VID/PID alone names a <i>model</i>, not a bridge, so binding on it would authorise probing
    /// every CH340 on the bench. At least one of a serial number or a physical port is required to
    /// narrow it to one device. A bridge offering neither cannot be bound, and the arm must fail
    /// rather than bind something ambiguous.
    /// </para>
    /// <para>
    /// Which one it gets changes what the bind follows. With a serial, the bind follows the
    /// <i>device</i> and survives being moved to another socket. Without one it follows the
    /// <i>socket</i>, so an identical model plugged in there matches — the accepted cost of a
    /// bridge that offers nothing better.
    /// </para>
    /// </summary>
    public static bool TryFrom(DeviceInfo device, out BridgeIdentity identity, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        identity = default;

        if (device.VendorId is not { } vid || device.ProductId is not { } pid)
        {
            reason = $"device '{device.Name ?? device.Id.Value}' reports no USB vendor/product id, " +
                     "so there is nothing stable to bind autoflash to.";
            return false;
        }

        string? serial = Blank(device.SerialNumber) ? null : device.SerialNumber;
        string? location = Blank(device.LocationPath) ? null : device.LocationPath;

        if (serial is null && location is null)
        {
            reason = $"device '{device.Name ?? device.Id.Value}' ({vid}:{pid}) exposes neither a serial " +
                     "number nor a physical port, so it cannot be told apart from another of the same " +
                     "model. Autoflash will not bind to a bridge it cannot identify.";
            return false;
        }

        identity = new BridgeIdentity(vid, pid, serial, location);
        reason = null;
        return true;
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Whether this identity is pinned to the device itself rather than to the socket it sits in.
    /// </summary>
    public bool IsSerialBound => SerialNumber is not null;

    // One discriminator, not both. A serial names the bridge itself, so a bridge that moves to
    // another socket is still that bridge and must keep matching — requiring the location to agree
    // as well would silently unbind a fixture for being replugged, which is the opposite of what
    // binding is for. Location is the fallback for bridges that expose no serial (CH340s commonly
    // do not), and it binds to the socket: an identical model plugged in there does match, which is
    // the accepted cost of having nothing better to bind to.
    //
    // Both comparisons are case-insensitive for the reason DeviceId is (issue #231): Windows
    // re-enumerates the same device with different casing.
    /// <inheritdoc />
    public bool Equals(BridgeIdentity other)
    {
        if (VendorId != other.VendorId || ProductId != other.ProductId)
            return false;

        // A serial on one side and none on the other is two different devices, not a match.
        if (IsSerialBound || other.IsSerialBound)
            return string.Equals(SerialNumber, other.SerialNumber, StringComparison.OrdinalIgnoreCase);

        return string.Equals(LocationPath, other.LocationPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        VendorId,
        ProductId,
        StringComparer.OrdinalIgnoreCase.GetHashCode(SerialNumber ?? LocationPath ?? string.Empty));

    /// <inheritdoc />
    public override string ToString() =>
        $"{VendorId}:{ProductId}" +
        (SerialNumber is { } s ? $" SN {s}" : "") +
        (LocationPath is { } l ? $" @ {l}" : "");
}
