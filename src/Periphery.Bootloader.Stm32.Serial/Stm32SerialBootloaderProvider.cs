// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CallAndResponse;

namespace Periphery.Bootloader.Stm32.Serial;

/// <summary>
/// The <see cref="IBootloaderProvider"/> for the STM32 system UART bootloader (ST AN3155).
/// Register it with a <see cref="BootloaderRegistry"/> so the FlashAnything dispatcher can
/// resolve and open STM32 serial targets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identification is a probe, not an id.</b> A device in the system UART bootloader is behind a
/// USB-serial bridge (FTDI, CP210x, CH340) or a plain COM port, and the discovery metadata names
/// the bridge — never the STM32 behind it. So <see cref="CanHandle"/> claims any device that has a
/// serial port name, and the real identification is the AN3155 sync handshake that
/// <see cref="OpenAsync"/> performs. That is why <see cref="Identification"/> is
/// <see cref="IdentificationMode.Probe"/>: this provider is never eligible for unattended
/// autoflash, and a target reaches it only through an explicit manual action.
/// </para>
/// <para>
/// <b>Registration order matters.</b> Because the claim is broad, register this provider
/// <i>after</i> every VID/PID-matched provider — <see cref="BootloaderRegistry.Match"/> returns
/// the first claimant, so registering it earlier would let it swallow a serial device that a more
/// specific provider owns.
/// </para>
/// </remarks>
public sealed class Stm32SerialBootloaderProvider : IBootloaderProvider
{
    private readonly Stm32SerialOptions _options;
    private readonly ILogger<Transceiver>? _logger;

    /// <summary>Creates the provider with the given wire and timing settings.</summary>
    /// <param name="options">Settings for every programmer this opens; <see cref="Stm32SerialOptions.Default"/> when null.</param>
    /// <param name="logger">Optional logger forwarded to each programmer's transceiver.</param>
    public Stm32SerialBootloaderProvider(Stm32SerialOptions? options = null, ILogger<Transceiver>? logger = null)
    {
        _options = options ?? Stm32SerialOptions.Default;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "STM32 UART (AN3155)";

    /// <inheritdoc />
    public bool CanHandle(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.PortName is not null;
    }

    /// <inheritdoc />
    public async Task<IFirmwareProgrammer> OpenAsync(DeviceInfo device, CancellationToken ct = default)
        => await Stm32SerialProgrammer.OpenAsync(device, _options, _logger, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public IdentificationMode Identification => IdentificationMode.Probe; // the bridge's VID/PID is not the target
}
