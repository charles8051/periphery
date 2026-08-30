// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// The physical connector standard used to attach a monitor to its display adapter.
/// <para>
/// Derived on Windows from the Win32 DisplayConfig (CCD)
/// <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> of the monitor's active path
/// (ADR-0018). The Linux and macOS providers do not populate it, so
/// <see cref="DeviceInfo.DisplayPhysicalConnector"/> — the nullable property that
/// carries this value — is <see langword="null"/> there. A technology with no
/// faithful member here stays <see cref="Unknown"/> rather than being folded into
/// a neighbouring standard (ADR-0071).
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayConnector>))]
public enum DisplayConnector
{
    /// <summary>Connector type could not be determined.</summary>
    Unknown = 0,

    /// <summary>High-Definition Multimedia Interface.</summary>
    Hdmi,

    /// <summary>DisplayPort (including Mini DisplayPort).</summary>
    DisplayPort,

    /// <summary>Digital Visual Interface.</summary>
    Dvi,

    /// <summary>HD-15 / VGA (analogue).</summary>
    Vga,

    /// <summary>
    /// Low-voltage differential signalling (LVDS) — internal laptop or
    /// all-in-one panel.
    /// </summary>
    Internal,

    /// <summary>Analogue television / component video connector.</summary>
    AnalogTv,

    /// <summary>Serial Digital Interface (SDI).</summary>
    Sdi,
}
