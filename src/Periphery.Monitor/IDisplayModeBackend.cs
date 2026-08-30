// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>
/// Platform abstraction for the display-mode plane (resolution, orientation,
/// refresh) — ADR-0058 D7. Mode state lives in the OS display stack, not the
/// panel, so this plane is independent of <see cref="IMonitorBackend"/> and
/// may be present without it (virtual displays) or absent alongside it
/// (Linux today, per D9).
/// </summary>
internal interface IDisplayModeBackend : IAsyncDisposable
{
    Task<DisplayMode> GetCurrentModeAsync(CancellationToken ct);
    Task<IReadOnlyList<DisplayMode>> GetSupportedModesAsync(CancellationToken ct);
    Task SetModeAsync(DisplayMode mode, bool persist, CancellationToken ct);
    Task<MonitorOrientation> GetOrientationAsync(CancellationToken ct);
    Task SetOrientationAsync(MonitorOrientation orientation, bool persist, CancellationToken ct);
}
