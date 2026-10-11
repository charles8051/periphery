// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Periphery.Serial.Browser.Core;

/// <summary>A Web Serial promise rejected with a <c>DOMException</c>.</summary>
internal sealed record WebSerialRejection(string Name, string Message);

/// <summary>
/// The JSON the package and its JS module exchange. Hand-written over
/// <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>, so nothing depends on reflection the
/// WebAssembly trimmer could remove.
/// </summary>
internal static class WebSerialJson
{
    /// <summary>Writes <paramref name="filters"/> as the <c>filters</c> array of <c>requestPort()</c>.</summary>
    /// <exception cref="ArgumentException">A filter sets <c>UsbProductId</c> without <c>UsbVendorId</c>.</exception>
    public static string WriteFilters(IReadOnlyList<SerialPortRequestFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return Write(writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < filters.Count; i++)
            {
                var filter = filters[i] ?? throw new ArgumentException($"Filter {i} is null.", nameof(filters));
                if (filter.UsbProductId is not null && filter.UsbVendorId is null)
                    throw new ArgumentException($"Filter {i} sets UsbProductId without UsbVendorId, which Web Serial refuses.", nameof(filters));

                writer.WriteStartObject();
                if (filter.UsbVendorId is { } vendor) writer.WriteNumber("usbVendorId", vendor);
                if (filter.UsbProductId is { } product) writer.WriteNumber("usbProductId", product);
                if (filter.BluetoothServiceClassId is { } service) writer.WriteString("bluetoothServiceClassId", service.ToString("D"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    /// <summary>Writes <paramref name="options"/> as the <c>SerialOptions</c> of <c>open()</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is outside what Web Serial accepts.</exception>
    public static string WriteOptions(SerialPortOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BaudRate, nameof(options.BaudRate));
        if (options.DataBits is not (7 or 8))
            throw new ArgumentOutOfRangeException(nameof(options.DataBits), options.DataBits, "Web Serial supports 7 or 8 data bits.");
        if (options.StopBits is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(options.StopBits), options.StopBits, "Web Serial supports 1 or 2 stop bits.");
        if (options.BufferSize is <= 0 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.BufferSize), options.BufferSize, "Web Serial buffers hold 1 byte to 16 MiB.");

        string parity = options.Parity switch
        {
            SerialPortParity.None => "none",
            SerialPortParity.Even => "even",
            SerialPortParity.Odd => "odd",
            _ => throw new ArgumentOutOfRangeException(nameof(options.Parity), options.Parity, "Unknown parity."),
        };

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("baudRate", options.BaudRate);
            writer.WriteNumber("dataBits", options.DataBits);
            writer.WriteNumber("stopBits", options.StopBits);
            writer.WriteString("parity", parity);
            writer.WriteNumber("bufferSize", options.BufferSize);
            writer.WriteString("flowControl", options.HardwareFlowControl ? "hardware" : "none");
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Writes the <c>SerialOutputSignals</c> of <c>setSignals()</c>, with only the signals given.
    /// </summary>
    /// <exception cref="ArgumentException">No signal is given.</exception>
    public static string WriteSignals(bool? dataTerminalReady, bool? requestToSend, bool? breakSignal)
    {
        if (dataTerminalReady is null && requestToSend is null && breakSignal is null)
            throw new ArgumentException("Set at least one signal; Web Serial refuses an empty setSignals().");

        return Write(writer =>
        {
            writer.WriteStartObject();
            if (dataTerminalReady is { } dtr) writer.WriteBoolean("dataTerminalReady", dtr);
            if (requestToSend is { } rts) writer.WriteBoolean("requestToSend", rts);
            if (breakSignal is { } brk) writer.WriteBoolean("break", brk);
            writer.WriteEndObject();
        });
    }

    /// <summary>Reads <c>port.getInfo()</c> with the module's handle: <c>{"handle", "usbVendorId", "usbProductId"}</c>.</summary>
    /// <exception cref="FormatException">The JSON is not in that shape.</exception>
    public static (int Handle, ushort? UsbVendorId, ushort? UsbProductId) ReadPort(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (root.GetProperty("handle").GetInt32(), OptionalUInt16(root, "usbVendorId"), OptionalUInt16(root, "usbProductId"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new FormatException($"The port is not in the expected shape: {e.Message}", e);
        }
    }

    /// <summary>Reads a rejection the module caught: <c>{"name": ..., "message": ...}</c>.</summary>
    public static WebSerialRejection ReadRejection(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new WebSerialRejection(OptionalString(root, "name") ?? "Error", OptionalString(root, "message") ?? string.Empty);
        }
        catch (JsonException)
        {
            return new WebSerialRejection("Error", json);
        }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            body(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ushort? OptionalUInt16(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt16() : null;

    private static string? OptionalString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
