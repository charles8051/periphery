// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Linq;

namespace Periphery.Bootloader;

/// <summary>
/// The app-mode counterpart to <see cref="BootloaderRegistry"/>: holds the registered
/// <see cref="IBootloaderEntry"/>s and resolves which one can reboot a discovered
/// <em>application-mode</em> device into its bootloader. A device-specific flasher composition
/// registers its entries here; the FlashAnything dispatcher matches live application devices against
/// it (ADR-0063 DEC-003/DEC-004).
/// </summary>
public sealed class BootloaderEntryRegistry
{
    private readonly List<IBootloaderEntry> _entries = new();

    /// <summary>Registers an entry. Earlier registrations win ties in <see cref="Match"/>.</summary>
    public void Register(IBootloaderEntry entry) => _entries.Add(entry);

    /// <summary>The registered entries, in registration order.</summary>
    public IReadOnlyList<IBootloaderEntry> Entries => _entries;

    /// <summary>The first registered entry that can reboot <paramref name="applicationDevice"/>, or null.</summary>
    public IBootloaderEntry? Match(DeviceInfo applicationDevice) =>
        _entries.FirstOrDefault(e => e.CanEnter(applicationDevice));
}
