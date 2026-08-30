// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Microsoft.Extensions.Logging;
using Periphery.Bootloader;
using Periphery.Bootloader.Efm8.Usb;
using Periphery.FlashAnything;
using Periphery.Treehopper.Firmware;

namespace Periphery.Treehopper.Flasher;

/// <summary>
/// The "Treehopper Flasher" composition (ADR-0063 DEC-006): a device-specific flasher is the same
/// <see cref="FlashAnythingService"/> with a <b>curated registry and branding</b>, not a new app.
/// Here the curation is a Treehopper application entry, the EFM8 USB bootloader provider it
/// re-enumerates as, and the EFM8UB1 Intel-HEX converter — nothing else. The branded CLI and GUI are
/// thin front-ends over this one composition; a new device would be its own equally-thin composition.
/// </summary>
public static class TreehopperFlasher
{
    /// <summary>The product name, shown in branding (window title, CLI banner).</summary>
    public const string Name = "Treehopper Flasher";

    /// <summary>The branded CLI command name.</summary>
    public const string ToolCommand = "treehopper-flash";

    /// <summary>
    /// Builds a <see cref="FlashAnythingService"/> curated to Treehopper: it discovers and flashes
    /// Treehopper application devices (rebooting each into its EFM8 USB-HID bootloader, then flashing
    /// it — from a <c>.hex</c> or <c>.efm8</c>) and bare EFM8 bootloaders. No other families.
    /// </summary>
    /// <param name="logger">Optional sink for the discovery/flash trace.</param>
    /// <param name="entryOptions">
    /// Tunables for the reboot-into-bootloader step, forwarded with <b>one</b> exception:
    /// <see cref="BootloaderEntryOptions.Correlation"/> is <em>owned by this composition</em> and always
    /// set to <see cref="DeviceCorrelationMode.ByLocationPath"/> (topology correlation is the correct
    /// policy for the no-serial EFM8 bootloader — see <see cref="CreateService"/>), so a caller-supplied
    /// Correlation is intentionally overridden. Every other field is passed through. The one that matters
    /// in the field is <see cref="BootloaderEntryOptions.BootloaderTimeout"/>: a board that takes longer
    /// than the default 15s to re-enumerate as its EFM8 bootloader otherwise fails indistinguishably from
    /// one that never rebooted at all. <c>null</c> keeps the defaults.
    /// </param>
    /// <param name="allowConcurrentEfm8Flash">
    /// Whether several EFM8 boards may flash <em>in flight at once</em> (ADR-0063 DEC-005).
    /// <see cref="BootloaderEntryOptions.Correlation"/> is always
    /// <see cref="DeviceCorrelationMode.ByLocationPath"/> — correlation is exact and picks the right board
    /// every time regardless of this flag. Defaults to <c>true</c>: concurrent EFM8 flashing is
    /// hardware-verified safe (two boards, overlapping upload windows, zero corruption on Windows), and
    /// the #220 physical bus-collision hypothesis was <b>disproven</b> during investigation — two separate
    /// processes each flashing one board concurrently corrupted nothing (so the fault was in-process
    /// shared state, not the bus), and boards on separate USB controllers + a powered hub still failed
    /// identically (so it was never shared-bus power). The sole root cause was the software correlation
    /// collapse (two concurrent <see cref="DeviceCorrelationMode.FirstAppearance"/> waits both grabbing the
    /// first-appearing bootloader → interleaved writes), which <see cref="DeviceCorrelationMode.ByLocationPath"/>
    /// fixes at the root. Pass <c>false</c> to <b>opt out</b> — force serialize (one board at a time) — as a
    /// conservative fallback or a debugging aid.
    /// <para>
    /// Cross-platform: the hardware verification was on Windows; Linux (<c>syspath</c>) / macOS
    /// (<c>locationPath</c>) populate the same port field but their port-invariance across the mode switch
    /// is unverified. Concurrent-by-default is nonetheless acceptable there because
    /// <see cref="DeviceCorrelationMode.ByLocationPath"/> is an <em>exact</em> match: a wrong or absent
    /// port never mis-flashes — it simply fails to correlate and the wait times out (a clean
    /// "did not re-enumerate" error), so the failure mode is a visible timeout, never corruption or
    /// cross-correlation.
    /// </para>
    /// </param>
    /// <param name="resetSafetyGate">
    /// Consulted before any recovery reset (ADR-0060 Decision 4, ADR-0076). <c>null</c> means
    /// always-safe, which is right for a bench or a fleet-update window. A host that can be busy
    /// mid-operation — a kiosk taking a payment — should pass its own gate: a refusal fails the
    /// update with a clear reason instead of resetting the board under the operation.
    /// <para>
    /// Honoured whether or not <paramref name="entryOptions"/> already carries a
    /// <see cref="BootloaderEntryRecovery"/>: if that recovery has its own gate, the two are
    /// composed so <b>both</b> must permit the reset. This gate is never dropped in favour of
    /// another — a discarded veto is a reset the caller did not sanction.
    /// </para>
    /// </param>
    /// <param name="recoverWedgedBoards">
    /// Whether a board that will not enter its bootloader should be reset and retried
    /// (ADR-0076). Defaults to <c>true</c>, which is the whole point: the mode switch
    /// (<c>0x0D</c>, and the open that reconciles before it) travels over the peripheral-config
    /// endpoint, so a board whose foreground has stopped cannot be told to enter its bootloader
    /// at all — the updater's answer to a wedged board would otherwise be "flash it", which
    /// needs a board that works. With this on, the ladder's <c>SoftProtocolOutOfBand</c> rung
    /// reaches the board over EP0 (serviced by its USB ISR, not its dead foreground), the board
    /// reboots into a healthy application, and the retry succeeds.
    /// <para>
    /// Pass <c>false</c> to opt out and keep the pre-ADR-0076 behaviour, where the first failed
    /// mode switch fails the update. Worth doing when a reset is unacceptable and you would
    /// rather diagnose the board than have the tool disturb it.
    /// </para>
    /// </param>
    public static FlashAnythingService CreateService(
        ILogger? logger = null, BootloaderEntryOptions? entryOptions = null, bool allowConcurrentEfm8Flash = true,
        IResetSafetyGate? resetSafetyGate = null, bool recoverWedgedBoards = true)
    {
        // Correlate the re-enumerated EFM8 bootloader to the app it came from by USB topology: the EFM8
        // HID bootloader is the shared 0x10C4:0xEAC9 for every board (no serial), but a board does not
        // change physical port when it resets, so the bootloader shares the app device's LocationPath
        // (Windows-hardware-verified). ByLocationPath is exact and picks the right board every time,
        // superseding FirstAppearance (debounce), which collapses two concurrent waits onto the
        // first-appearing bootloader. This composition owns the correlation mode; every other
        // caller-supplied entry option — BootloaderTimeout and the settle/other handling from #220 — is
        // preserved verbatim.
        entryOptions = (entryOptions ?? new BootloaderEntryOptions()) with
        {
            Correlation = DeviceCorrelationMode.ByLocationPath,
        };

        // Recovery for a board that will not enter its bootloader (ADR-0076). TreehopperDeviceReset
        // is what makes this worth having: it advertises SoftProtocol (0x0C, over the bulk config
        // endpoint) and SoftProtocolOutOfBand (the EP0 vendor rescue, ADR-0075) ahead of the
        // platform's USB port-cycle / PnP rungs, so the ladder starts gentle and — crucially —
        // includes the one rung that still reaches a board whose foreground has stopped.
        //
        // Composed on the OUTSIDE of the platform reset, per TreehopperDeviceReset's own placement
        // rule: the soft rungs are board-protocol commands that must run where the board physically
        // is, and only the harder cfgmgr32-style rungs fall through to the inner reset.
        //
        // The RECOVERY MECHANISM is deliberately not overridden when the caller already supplied
        // one: this composition owns Correlation (above) because a wrong correlation mis-flashes,
        // but the reset and policy are choices about disturbing hardware, and a caller who has
        // expressed one means it.
        //
        // THE SAFETY GATE IS NOT LIKE THAT. It is a veto, and a veto that is silently discarded is
        // worse than no veto at all — the caller believes they are protected and are not. So the
        // gate passed here is always composed in, whichever recovery is in play: with a
        // caller-supplied gate it becomes "both must permit", never "the other one wins".
        if (recoverWedgedBoards && entryOptions.Recovery is null)
        {
            entryOptions = entryOptions with
            {
                Recovery = new BootloaderEntryRecovery(
                    new TreehopperDeviceReset(DeviceReset.PlatformDefault, loggerFactory: null),
                    Policy: null,                     // EscalatingResetRecoveryPolicy.Default
                    SafetyGate: resetSafetyGate),
            };
        }
        else if (entryOptions.Recovery is { } supplied && resetSafetyGate is not null)
        {
            entryOptions = entryOptions with
            {
                Recovery = supplied with
                {
                    SafetyGate = ResetSafetyGate.All(supplied.SafetyGate, resetSafetyGate),
                },
            };
        }

        // Concurrency is a SEPARATE axis from correlation, and it is now the DEFAULT. Concurrent EFM8
        // flashing is hardware-verified (two boards, overlapping upload windows, zero corruption on
        // Windows), and #220's physical bus-collision hypothesis was disproven: the corruption was purely
        // the in-process correlation collapse (two FirstAppearance waits grabbing the same first-appearing
        // bootloader), which ByLocationPath fixes at the root. So the full pool runs by default; passing
        // allowConcurrentEfm8Flash: false is the opt-OUT that forces one-board-at-a-time serialization.
        int maxFlashConcurrency = allowConcurrentEfm8Flash ? FlashAnythingService.DefaultMaxFlashConcurrency : 1;

        // The reusable EFM8 bootloader flasher (a Treehopper re-enumerates as 0x10C4:0xEAC9).
        var registry = new BootloaderRegistry();
        registry.Register(new Efm8UsbBootloaderProvider());

        // The device-specific bit: wake a Treehopper application device into that bootloader.
        var entries = new BootloaderEntryRegistry();
        entries.Register(new TreehopperBootloaderEntry());

        // A Treehopper's EFM8 is a UB1, so a toolchain .hex converts to boot records on the way in.
        var converters = new FirmwareConverterRegistry();
        converters.Register(new Efm8IntelHexConverter(Efm8BootOptions.Ub1));

        return new FlashAnythingService(
            registry, maxFlashConcurrency: maxFlashConcurrency, logger: logger,
            entries: entries, converters: converters, entryOptions: entryOptions);
    }
}
