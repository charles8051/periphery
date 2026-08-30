// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Camera;

/// <summary>
/// Identifies the memory domain where a camera frame's pixel data resides.
/// </summary>
/// <remarks>
/// <para>
/// Periphery-local enum, independent of any substrate. Consumers that
/// integrate with FrameFlow.Graph (via <c>FrameFlow.Camera</c>) map this
/// onto <c>FrameFlow.Graph.FrameMemoryDomain</c> at the adapter boundary
/// if they need substrate-typed dispatch.
/// </para>
/// <para>
/// v1 supports only <see cref="Cpu"/>. GPU-resident domains will be added
/// when Periphery.Camera grows a hardware-decode path.
/// </para>
/// </remarks>
public enum CameraFrameMemoryDomain
{
    /// <summary>Frame data is in CPU-accessible system memory.</summary>
    Cpu,
}
