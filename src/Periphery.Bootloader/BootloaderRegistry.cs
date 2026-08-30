// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Generic;
using System.Linq;

namespace Periphery.Bootloader;

/// <summary>
/// The "flash anything" dispatcher: holds the registered
/// <see cref="IBootloaderProvider"/>s and resolves which one handles a discovered
/// device. Flasher packages register their provider here; the FlashAnything app matches
/// live devices against it.
/// </summary>
public sealed class BootloaderRegistry
{
    private readonly List<IBootloaderProvider> _providers = new();

    /// <summary>Registers a provider. Earlier registrations win ties in <see cref="Match"/>.</summary>
    public void Register(IBootloaderProvider provider) => _providers.Add(provider);

    /// <summary>The registered providers, in registration order.</summary>
    public IReadOnlyList<IBootloaderProvider> Providers => _providers;

    /// <summary>The first registered provider that can handle <paramref name="device"/>, or null.</summary>
    public IBootloaderProvider? Match(DeviceInfo device) =>
        _providers.FirstOrDefault(p => p.CanHandle(device));
}
