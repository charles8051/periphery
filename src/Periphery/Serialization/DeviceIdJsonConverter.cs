// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Periphery;

/// <summary>
/// Serializes <see cref="DeviceId"/> as a plain string (the platform-native
/// instance id, e.g. <c>"USB\\VID_046D&amp;PID_C52B\\6&amp;1a2b3c4d&amp;0&amp;2"</c>),
/// so the wire/JSON representation is a bare string rather than an object.
/// </summary>
public sealed class DeviceIdJsonConverter : JsonConverter<DeviceId>
{
    /// <inheritdoc/>
    public override DeviceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DeviceId.Parse(reader.GetString()!);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DeviceId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
