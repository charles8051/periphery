// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Periphery.Usb.Browser.Core;

/// <summary>
/// The JSON the package and its JS module exchange: request filters out, device facts and
/// rejections in. Hand-written over <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>,
/// so nothing depends on reflection the WebAssembly trimmer could remove.
/// </summary>
internal static class WebUsbJson
{
    /// <summary>
    /// Writes <paramref name="filters"/> as the <c>filters</c> array of
    /// <c>USBDeviceRequestOptions</c>, after checking each against WebUSB's rules.
    /// </summary>
    /// <exception cref="ArgumentException">A filter sets a field without the one it requires.</exception>
    public static string WriteFilters(IReadOnlyList<UsbDeviceRequestFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (int i = 0; i < filters.Count; i++)
            {
                var filter = filters[i] ?? throw new ArgumentException($"Filter {i} is null.", nameof(filters));

                if (filter.ProductId is not null && filter.VendorId is null)
                    throw new ArgumentException($"Filter {i} sets ProductId without VendorId, which WebUSB refuses.", nameof(filters));
                if (filter.SubclassCode is not null && filter.ClassCode is null)
                    throw new ArgumentException($"Filter {i} sets SubclassCode without ClassCode, which WebUSB refuses.", nameof(filters));
                if (filter.ProtocolCode is not null && filter.SubclassCode is null)
                    throw new ArgumentException($"Filter {i} sets ProtocolCode without SubclassCode, which WebUSB refuses.", nameof(filters));

                writer.WriteStartObject();
                if (filter.VendorId is { } vendor) writer.WriteNumber("vendorId", vendor.Value);
                if (filter.ProductId is { } product) writer.WriteNumber("productId", product.Value);
                if (filter.ClassCode is { } cls) writer.WriteNumber("classCode", cls);
                if (filter.SubclassCode is { } subclass) writer.WriteNumber("subclassCode", subclass);
                if (filter.ProtocolCode is { } protocol) writer.WriteNumber("protocolCode", protocol);
                if (filter.SerialNumber is { } serial) writer.WriteString("serialNumber", serial);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads the module's report of <c>getDevices()</c>: an array of objects carrying a
    /// <c>handle</c> and the <c>USBDevice</c> fields <see cref="UsbGrantFacts"/> holds.
    /// </summary>
    /// <exception cref="FormatException">The JSON is not such an array.</exception>
    public static IReadOnlyList<(GrantHandle Handle, UsbGrantFacts Facts)> ReadDevices(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("The device list is not a JSON array.");

            var devices = new List<(GrantHandle, UsbGrantFacts)>();
            foreach (var device in document.RootElement.EnumerateArray())
            {
                var facts = new UsbGrantFacts
                {
                    VendorId = device.GetProperty("vendorId").GetUInt16(),
                    ProductId = device.GetProperty("productId").GetUInt16(),
                    SerialNumber = OptionalString(device, "serialNumber"),
                    ProductName = OptionalString(device, "productName"),
                    ManufacturerName = OptionalString(device, "manufacturerName"),
                    DeviceClass = device.GetProperty("deviceClass").GetByte(),
                    DeviceSubclass = device.GetProperty("deviceSubclass").GetByte(),
                    DeviceProtocol = device.GetProperty("deviceProtocol").GetByte(),
                };
                devices.Add((new GrantHandle(device.GetProperty("handle").GetInt32()), facts));
            }
            return devices;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"The device list is not in the expected shape: {e.Message}", e);
        }
    }

    /// <summary>Reads a rejection the module caught: <c>{"name": ..., "message": ...}</c>.</summary>
    public static WebUsbRejection ReadRejection(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new WebUsbRejection(
                OptionalString(root, "name") ?? "Error",
                OptionalString(root, "message") ?? string.Empty);
        }
        catch (JsonException)
        {
            return new WebUsbRejection("Error", json);
        }
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
