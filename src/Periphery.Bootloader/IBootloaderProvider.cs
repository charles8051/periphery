// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader;

/// <summary>
/// Recognises devices it can flash and opens a programmer for them. Each flasher package
/// (<c>Periphery.Bootloader.Stm32.Usb</c>, <c>.Efm8.Usb</c>, ...) ships one provider and
/// registers it with a <see cref="BootloaderRegistry"/> — the seam the "flash anything"
/// dispatcher resolves a discovered <see cref="DeviceInfo"/> against.
/// </summary>
public interface IBootloaderProvider
{
    /// <summary>Human-readable provider name, e.g. <c>"STM32 USB DFU"</c>.</summary>
    string Name { get; }

    /// <summary>True if this provider can flash <paramref name="device"/> (VID/PID or signature match).</summary>
    bool CanHandle(DeviceInfo device);

    /// <summary>Opens a bootloader session for <paramref name="device"/>.</summary>
    Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default);

    /// <summary>
    /// How this provider establishes a target's identity. <see cref="IdentificationMode.Passive"/>
    /// families (USB VID/PID) are eligible for unattended autoflash; <see cref="IdentificationMode.Probe"/>
    /// families (serial, where the VID/PID names only the USB bridge) are flashed solely by an
    /// explicit manual action. See the autoflash feature spec.
    /// </summary>
    IdentificationMode Identification { get; }
}

/// <summary>
/// How a provider establishes a device's identity — the load-bearing autoflash safety gate.
/// </summary>
public enum IdentificationMode
{
    /// <summary>
    /// Identified passively from discovery metadata (USB VID/PID) without touching the device:
    /// the id <em>is</em> the target (0483:DF11 is an STM32 in DFU). Safe to act on unattended.
    /// </summary>
    Passive,

    /// <summary>
    /// Identifying the real target needs an active probe (serial autobaud / sync), because
    /// discovery names only the USB-serial bridge (FTDI / CP210x / CH340), not what is behind it.
    /// Never auto-flashed.
    /// </summary>
    Probe,
}
