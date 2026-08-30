// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The transport seam (ADR-0052): the seven DFU class requests at the request grain. The
/// production implementation (<see cref="UsbStm32DfuTransport"/>) issues USB control
/// transfers; tests substitute a fake to drive the shell with no hardware.
/// </summary>
internal interface IStm32DfuTransport
{
    /// <summary>DFU_DNLOAD: send a block (or, with <c>wBlockNum = 0</c>, a command payload).</summary>
    Task<int> DownloadAsync(ushort blockNum, ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>DFU_UPLOAD: read a block (or, with <c>wBlockNum = 0</c>, the Get command list).</summary>
    Task<int> UploadAsync(ushort blockNum, Memory<byte> buffer, CancellationToken ct);

    /// <summary>DFU_GETSTATUS: the 6-byte status (also triggers a pending DNLOAD command).</summary>
    Task<DfuStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>DFU_CLRSTATUS: clear an error and return toward dfuIDLE.</summary>
    Task ClearStatusAsync(CancellationToken ct);

    /// <summary>DFU_GETSTATE: the 1-byte state, without side effects.</summary>
    Task<DfuState> GetStateAsync(CancellationToken ct);

    /// <summary>DFU_ABORT: return to dfuIDLE from a non-error state.</summary>
    Task AbortAsync(CancellationToken ct);
}
