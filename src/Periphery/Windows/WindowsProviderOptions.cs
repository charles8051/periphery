// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Configuration options for the Windows device provider.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record WindowsProviderOptions
{
}
