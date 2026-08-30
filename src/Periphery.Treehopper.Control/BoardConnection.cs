// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Control;

/// <summary>How a board is currently presenting itself on the bus.</summary>
public enum BoardConnection
{
    /// <summary>Running the Treehopper application firmware (<c>0x10C4:0x8A7E</c>).</summary>
    Application,

    /// <summary>In the EFM8 HID bootloader (<c>0x10C4:0xEAC9</c>) — typically mid-reflash.</summary>
    Bootloader,
}
