// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Bootloader.Efm8.Usb;

/// <summary>
/// A point-in-time progress snapshot reported after each acknowledged record.
/// </summary>
/// <param name="RecordsSent">Records acknowledged so far.</param>
/// <param name="TotalRecords">Total records in the stream.</param>
/// <param name="BytesSent">Frame bytes acknowledged so far.</param>
/// <param name="TotalBytes">Total frame bytes in the stream.</param>
public readonly record struct Efm8UploadProgress(
    int RecordsSent, int TotalRecords, int BytesSent, int TotalBytes)
{
    /// <summary>Completion as a percentage of records, 0–100.</summary>
    public double Percent => TotalRecords == 0 ? 100.0 : 100.0 * RecordsSent / TotalRecords;
}
