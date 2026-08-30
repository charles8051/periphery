// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Periphery.Usb;

namespace Periphery.Treehopper;

/// <summary>
/// A Treehopper-aware <see cref="IDeviceReset"/> decorator that adds the two soft rungs the
/// core platform reset cannot supply (ADR-0060: a device-specific soft reset is "owned by a
/// device extension, not core"; ADR-0075 adds the second). For a Treehopper board it prepends
/// both — gentlest-first, ahead of the platform's USB port-cycle / PnP disable-enable rungs:
/// <list type="number">
///   <item>
///     <see cref="ResetKind.SoftProtocol"/> — open the board and issue
///     <see cref="TreehopperBoard.RebootAsync"/> (wire opcode <c>0x0C</c>, the port of the
///     original SDK's <c>TreehopperUsb.Reboot()</c>).
///   </item>
///   <item>
///     <see cref="ResetKind.SoftProtocolOutOfBand"/> — the EP0 vendor rescue, for when the
///     first rung cannot be delivered at all.
///   </item>
/// </list>
/// Every other device and every other strategy delegate unchanged to the wrapped reset.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these rungs matter.</b> The USB-level rungs (<see cref="ResetKind.UsbPortCycle"/>,
/// <see cref="ResetKind.PnpDisableEnable"/>) only re-enumerate the host's view of the
/// device — they cannot reset the board's MCU firmware. A wedged firmware endpoint (the
/// EFM8 bulk-OUT hang seen on a deployed LED strip) survives both: the board
/// re-opens cleanly but the first SPI write faults again within seconds. Only a firmware
/// reset clears that, and the gentlest one belongs first.
/// </para>
/// <para>
/// <b>Why there are two of them.</b> The <c>0x0C</c> reboot travels over the peripheral-config
/// bulk endpoint, which the firmware re-arms <em>only from its foreground superloop</em>. When
/// that superloop has stopped — the field failure mode, and the one ADR-0060 was written for —
/// the reboot is delivered to the very endpoint that is wedged and never arrives. The
/// out-of-band rung is a vendor request on EP0, which the device services from its USB ISR, so
/// it still gets through in exactly that state. The distinction is reachability, not force,
/// which is why they are separate strategies a policy can choose between rather than one rung
/// with a hidden fallback (ADR-0075 DEC-002).
/// </para>
/// <para>
/// <b>The out-of-band rung is a no-op on firmware that predates the handler</b>, and reports
/// <see cref="ResetOutcome.Issued"/> anyway, because a resetting board and one that never
/// implemented the request fault identically. It is not a remedy for boards already wedged in
/// the field on older firmware — see ADR-0075's consequences.
/// </para>
/// <para>
/// <b>Placement — wrap on the OUTSIDE.</b> <see cref="ResetKind.SoftProtocol"/> is a
/// board-protocol command that must run wherever the board physically is (locally), so it
/// must NOT be routed through a remote/privileged reset adapter. Compose this decorator
/// <em>around</em> any such adapter so the soft rung is handled here and only the harder,
/// cfgmgr32-style rungs fall through to the inner reset.
/// </para>
/// <para>
/// <b>No elevation required.</b> Opening a USB board and sending a command needs no
/// elevation, so the soft rung self-heals a firmware wedge even on a non-elevated,
/// unsupervised host where the platform's disable/enable rung would fail.
/// </para>
/// <para>
/// <see cref="ResetAsync"/> never throws on a board/transport problem: a failed open or a
/// faulted reboot write maps to <see cref="ResetOutcome.Failed"/> so the recovery loop
/// escalates to the next rung. Cancellation still propagates.
/// </para>
/// </remarks>
public sealed class TreehopperDeviceReset : IDeviceReset
{
    // The soft rungs the core platform reset never advertises, gentlest first.
    //
    // ReEnumerates: true on both — either reset drops the USB device and it comes back
    // (~230 ms absent, measured; periphery #232), so the watcher fast-path is available.
    // The proxy self-drives the reopen regardless (ADR-0060 Decision 9).
    private static readonly ResetStrategy SoftReset =
        new(ResetKind.SoftProtocol, ResetBlastRadius.Self, ReEnumerates: true);

    // The rung that survives a wedged foreground (ADR-0075). SoftReset travels over the
    // peripheral-config bulk endpoint, which the firmware re-arms only from its superloop;
    // when that superloop has stopped, the reboot goes to the very endpoint that is wedged.
    // This one is a vendor request on EP0, serviced from the device's USB ISR, so it still
    // arrives in exactly that state.
    private static readonly ResetStrategy OutOfBandReset =
        new(ResetKind.SoftProtocolOutOfBand, ResetBlastRadius.Self, ReEnumerates: true);

    private readonly IDeviceReset _inner;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TreehopperDeviceReset> _logger;

    /// <param name="inner">
    /// The reset to delegate non-soft strategies (and all non-Treehopper devices) to —
    /// typically <see cref="DeviceReset.PlatformDefault"/> or a privileged/remote adapter
    /// composed around it.
    /// </param>
    /// <param name="loggerFactory">
    /// Mints the transient board's diagnostics during a soft reset and this decorator's
    /// own logger. Defaults to <see cref="NullLoggerFactory"/>.
    /// </param>
    public TreehopperDeviceReset(IDeviceReset inner, ILoggerFactory? loggerFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<TreehopperDeviceReset>();
    }

    /// <inheritdoc/>
    public IReadOnlyList<ResetStrategy> StrategiesFor(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var inner = _inner.StrategiesFor(device);
        if (!IsTreehopper(device))
            return inner;

        // Prepend both soft rungs, gentlest-first: the cooperative reboot, then the
        // out-of-band rescue that survives a wedged foreground, then the platform's
        // harder rungs in the order the inner reset advertised them.
        var strategies = new ResetStrategy[inner.Count + 2];
        strategies[0] = SoftReset;
        strategies[1] = OutOfBandReset;
        for (int i = 0; i < inner.Count; i++)
            strategies[i + 2] = inner[i];
        return strategies;
    }

    /// <inheritdoc/>
    public ValueTask<ResetOutcome> ResetAsync(DeviceInfo device, ResetStrategy strategy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (IsTreehopper(device))
        {
            if (strategy.Kind == ResetKind.SoftProtocol) return SoftRebootAsync(device, ct);
            if (strategy.Kind == ResetKind.SoftProtocolOutOfBand) return OutOfBandRescueAsync(device, ct);
        }

        return _inner.ResetAsync(device, strategy, ct);
    }

    // The out-of-band rung (ADR-0075). Deliberately does NOT go through TreehopperBoard.OpenAsync
    // the way SoftRebootAsync does: opening a board reconciles its configuration over the
    // peripheral-config endpoint, which is the endpoint a wedged board is not draining, so the open
    // would time out and this rung would fail on exactly the boards it exists for. The static
    // rescue opens the USB device and issues the EP0 request, touching no bulk endpoint.
    //
    // Always Issued, never Failed: the request cannot be confirmed or refuted from the transfer
    // (ADR-0075 DEC-004), so the only honest report is that it was issued. Firmware without the
    // handler therefore also reports Issued and does nothing — the recovery loop's re-open will not
    // succeed and it escalates to the next rung on its own budget, which is the intended behaviour.
    // Only a failure to open the device at all is a real Failed.
    private async ValueTask<ResetOutcome> OutOfBandRescueAsync(DeviceInfo device, CancellationToken ct)
    {
        try
        {
            await TreehopperBoard.RescueResetAsync(device, ct, _loggerFactory).ConfigureAwait(false);
            _logger.LogInformation(
                "Out-of-band reset (EP0 rescue) issued for {Id}. This cannot be confirmed from the "
              + "transfer; watch for re-enumeration to tell whether the board took it.", device.Id);
            return ResetOutcome.Issued;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The USB device could not be opened at all — the request never left the host. That is
            // a genuine failure, distinct from the un-confirmable outcome of a request that did go.
            _logger.LogWarning(ex,
                "Out-of-band reset (EP0 rescue) could not open {Id}; escalating to the next reset strategy.",
                device.Id);
            return ResetOutcome.Failed;
        }
    }

    // Open a transient handle on the (closed) board, issue the reboot opcode, dispose
    // tolerantly. The proxy has already closed its own session and entered Resetting
    // before this runs, so the board is free to open; after the reboot the USB link drops
    // and re-enumerates, the proxy self-drives the reopen.
    private async ValueTask<ResetOutcome> SoftRebootAsync(DeviceInfo device, CancellationToken ct)
    {
        TreehopperBoard? board = null;
        try
        {
            board = await TreehopperBoard.OpenAsync(device, ct, _loggerFactory).ConfigureAwait(false);
            await board.RebootAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Soft reset (board reboot) issued for {Id}; the board will drop and re-enumerate.", device.Id);
            return ResetOutcome.Issued;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Could not open the board or the reboot write faulted (e.g. the config endpoint
            // is wedged too). Report Failed so the recovery loop escalates to the next rung.
            _logger.LogWarning(ex,
                "Soft reset (board reboot) failed for {Id}; escalating to the next reset strategy.", device.Id);
            return ResetOutcome.Failed;
        }
        finally
        {
            if (board is not null)
            {
                try { await board.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // The USB link is dropping as the board reboots — a close fault here is expected.
                    _logger.LogDebug(ex, "Soft reset: disposing the transient board handle for {Id} faulted (expected as the link drops).", device.Id);
                }
            }
        }
    }

    private static bool IsTreehopper(DeviceInfo device) =>
        device.VendorId == TreehopperBoard.Vid && device.ProductId == TreehopperBoard.Pid;
}
