// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Monitor;

/// <summary>
/// Platform abstraction for the DDC/CI (VCP) control plane — ADR-0058 D3.
/// The primitive is deliberately thin: raw VCP get/set plus the MCCS
/// capabilities string. All semantics (brightness normalization, power and
/// input mapping, capabilities parsing) live above this seam, shared by every
/// platform.
/// </summary>
internal interface IMonitorBackend : IAsyncDisposable
{
    /// <summary>Reads one VCP feature's current and maximum values.</summary>
    Task<VcpFeatureValue> GetVcpFeatureAsync(byte code, CancellationToken ct);

    /// <summary>Writes one VCP feature value.</summary>
    Task SetVcpFeatureAsync(byte code, ushort value, CancellationToken ct);

    /// <summary>Fetches the raw MCCS capabilities string.</summary>
    Task<string> GetCapabilitiesStringAsync(CancellationToken ct);
}
