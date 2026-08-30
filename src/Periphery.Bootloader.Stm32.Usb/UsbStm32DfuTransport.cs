// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Threading;
using System.Threading.Tasks;
using Periphery.Usb;

namespace Periphery.Bootloader.Stm32.Usb;

/// <summary>
/// The production <see cref="IStm32DfuTransport"/>: each DFU class request is a USB control
/// transfer on endpoint 0 over a <see cref="UsbDevice"/> (ADR-0061 DEC-005). The shell
/// owns the device handle; this is the pure interpreter that ships the bytes.
/// </summary>
internal sealed class UsbStm32DfuTransport : IStm32DfuTransport
{
    private const byte HostToDevice = 0x21; // OUT, class, recipient = interface
    private const byte DeviceToHost = 0xA1; // IN,  class, recipient = interface

    private readonly UsbDevice _usb;
    private readonly ushort _interface;

    public UsbStm32DfuTransport(UsbDevice usb, byte interfaceNumber)
    {
        _usb = usb;
        _interface = interfaceNumber;
    }

    public Task<int> DownloadAsync(ushort blockNum, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = HostToDevice, Request = (byte)DfuRequest.Dnload, Value = blockNum, Index = _interface },
            data.ToArray(), ct);

    public Task<int> UploadAsync(ushort blockNum, Memory<byte> buffer, CancellationToken ct) =>
        _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = DeviceToHost, Request = (byte)DfuRequest.Upload, Value = blockNum, Index = _interface },
            buffer, ct);

    public async Task<DfuStatus> GetStatusAsync(CancellationToken ct)
    {
        var buffer = new byte[6];
        int read = await _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = DeviceToHost, Request = (byte)DfuRequest.GetStatus, Value = 0, Index = _interface },
            buffer, ct).ConfigureAwait(false);
        if (read < 6)
            throw new Stm32DfuException($"DFU_GETSTATUS returned {read} bytes (expected 6).");
        return DfuStatus.Decode(buffer);
    }

    public Task ClearStatusAsync(CancellationToken ct) =>
        _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = HostToDevice, Request = (byte)DfuRequest.ClrStatus, Value = 0, Index = _interface },
            Memory<byte>.Empty, ct);

    public async Task<DfuState> GetStateAsync(CancellationToken ct)
    {
        var buffer = new byte[1];
        int read = await _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = DeviceToHost, Request = (byte)DfuRequest.GetState, Value = 0, Index = _interface },
            buffer, ct).ConfigureAwait(false);
        return read >= 1 ? (DfuState)buffer[0] : DfuState.DfuError;
    }

    public Task AbortAsync(CancellationToken ct) =>
        _usb.ControlTransferAsync(
            new UsbControlSetup { RequestType = HostToDevice, Request = (byte)DfuRequest.Abort, Value = 0, Index = _interface },
            Memory<byte>.Empty, ct);
}
