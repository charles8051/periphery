// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Monitor;

/// <summary>
/// One VCP feature read: the current value and the maximum the monitor
/// reports for it. For non-continuous (table) features the maximum is not
/// meaningful as a range bound — interpret <see cref="Current"/> against the
/// feature's value table instead.
/// </summary>
public readonly record struct VcpFeatureValue(ushort Current, ushort Maximum);
