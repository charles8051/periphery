// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Bootloader;

namespace Periphery.DebugProbe.StLink;

/// <summary>
/// The <see cref="IBootloaderProvider"/> for an ST-Link's debug function. The probe's VID and
/// PID name the probe, not the chip behind it, so it is <see cref="IdentificationMode.Probe"/>
/// and never auto-flashed (ADR-0094 D4).
/// </summary>
public sealed class StLinkProvider : IBootloaderProvider
{
    /// <inheritdoc />
    public string Name => "ST-Link (SWD)";

    /// <inheritdoc />
    public IdentificationMode Identification => IdentificationMode.Probe;

    /// <inheritdoc />
    public bool CanHandle(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsDebugFunction(device);
    }

    /// <inheritdoc />
    public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default) =>
        await StLinkProgrammer.OpenAsync(device, ct).ConfigureAwait(false);

    /// <summary>
    /// An ST-Link PID, and the function that carries the debug interface: interface 0 of a
    /// composite probe (<c>&amp;MI_00</c> on Windows), or a single-function probe. The composite
    /// parent, which Windows binds to <c>usbccgp</c>, is not it.
    /// </summary>
    internal static bool IsDebugFunction(DeviceInfo device)
    {
        if (device.VendorId?.Value != StLinkProtocol.VendorId
            || device.ProductId is not { } pid
            || !StLinkProtocol.Models.ContainsKey((ushort)pid.Value))
            return false;

        string id = device.Id.Value;
        int mi = id.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (mi >= 0)
            return id.AsSpan(mi + 4).StartsWith("00", StringComparison.Ordinal);
        return !string.Equals(device.Driver, "usbccgp", StringComparison.OrdinalIgnoreCase);
    }
}
