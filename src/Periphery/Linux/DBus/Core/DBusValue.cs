// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;

namespace Periphery.Linux.DBus.Core;

/// <summary>
/// One decoded D-Bus value (ADR-0091 D2). The codec turns a message body into these and back, and
/// nothing in the tree refers to the buffer it came from.
/// </summary>
internal abstract record DBusValue
{
    private protected DBusValue() { }
}

/// <summary>A string (<c>s</c>), object path (<c>o</c>) or signature (<c>g</c>).</summary>
internal sealed record DBusString(char Code, string Value) : DBusValue;

/// <summary>A boolean (<c>b</c>).</summary>
internal sealed record DBusBoolean(bool Value) : DBusValue;

/// <summary>
/// An integer that fits a <see cref="long"/>: <c>y</c>, <c>n</c>, <c>q</c>, <c>i</c>, <c>u</c>,
/// <c>x</c>, or a Unix fd index <c>h</c>.
/// </summary>
internal sealed record DBusInteger(char Code, long Value) : DBusValue;

/// <summary>An unsigned 64-bit integer (<c>t</c>).</summary>
internal sealed record DBusUInt64(ulong Value) : DBusValue;

/// <summary>A double (<c>d</c>).</summary>
internal sealed record DBusDouble(double Value) : DBusValue;

/// <summary>A byte array (<c>ay</c>), kept as bytes rather than one value per element.</summary>
internal sealed record DBusBytes(ImmutableArray<byte> Value) : DBusValue;

/// <summary>An array of any element type other than <c>y</c>.</summary>
internal sealed record DBusArray(string ElementSignature, ImmutableArray<DBusValue> Items) : DBusValue;

/// <summary>A struct: <c>(…)</c>.</summary>
internal sealed record DBusStruct(ImmutableArray<DBusValue> Fields) : DBusValue;

/// <summary>A dict entry, which appears only as an array element: <c>a{…}</c>.</summary>
internal sealed record DBusDictEntry(DBusValue Key, DBusValue Value) : DBusValue;

/// <summary>A variant (<c>v</c>): a value that carries its own single complete type.</summary>
internal sealed record DBusVariant(string Signature, DBusValue Value) : DBusValue;
