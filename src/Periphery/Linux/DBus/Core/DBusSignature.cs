// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Linux.DBus.Core;

/// <summary>
/// Type signatures, per the D-Bus specification's "Type System" section.
/// </summary>
internal static class DBusSignature
{
    /// <summary>The longest signature the specification allows.</summary>
    internal const int MaxLength = 255;

    /// <summary>The deepest array nesting, and separately the deepest struct nesting, allowed.</summary>
    internal const int MaxNesting = 32;

    /// <summary>
    /// Checks that <paramref name="signature"/> is a sequence of complete types.
    /// </summary>
    /// <exception cref="DBusProtocolException">The signature is malformed.</exception>
    internal static void Validate(string signature)
    {
        if (signature.Length > MaxLength)
            throw new DBusProtocolException($"Signature is {signature.Length} characters; the limit is {MaxLength}.");

        int index = 0;
        while (index < signature.Length)
            index = SkipCompleteType(signature, index, 0, 0);
    }

    /// <summary>
    /// Checks that <paramref name="signature"/> is exactly one complete type, as a variant carries.
    /// </summary>
    /// <exception cref="DBusProtocolException">The signature is malformed or holds more than one type.</exception>
    internal static void ValidateSingle(string signature)
    {
        if (signature.Length == 0)
            throw new DBusProtocolException("A variant's signature is empty.");

        Validate(signature);
        if (SkipCompleteType(signature, 0, 0, 0) != signature.Length)
            throw new DBusProtocolException($"A variant's signature '{signature}' holds more than one type.");
    }

    /// <summary>
    /// Returns the index just past the single complete type that starts at <paramref name="start"/>.
    /// </summary>
    /// <exception cref="DBusProtocolException">The type is malformed or nested too deeply.</exception>
    internal static int SkipCompleteType(string signature, int start, int arrayDepth, int structDepth)
    {
        if (start >= signature.Length)
            throw new DBusProtocolException($"Signature '{signature}' ends inside a type.");

        char code = signature[start];
        switch (code)
        {
            case 'y': case 'b': case 'n': case 'q': case 'i': case 'u': case 'x': case 't':
            case 'd': case 's': case 'o': case 'g': case 'h': case 'v':
                return start + 1;

            case 'a':
                if (arrayDepth + 1 > MaxNesting)
                    throw new DBusProtocolException($"Signature '{signature}' nests arrays deeper than {MaxNesting}.");
                if (start + 1 < signature.Length && signature[start + 1] == '{')
                    return SkipDictEntry(signature, start + 1, arrayDepth + 1, structDepth);
                return SkipCompleteType(signature, start + 1, arrayDepth + 1, structDepth);

            case '(':
            {
                if (structDepth + 1 > MaxNesting)
                    throw new DBusProtocolException($"Signature '{signature}' nests structs deeper than {MaxNesting}.");
                int index = start + 1;
                if (index < signature.Length && signature[index] == ')')
                    throw new DBusProtocolException($"Signature '{signature}' has an empty struct.");
                while (index < signature.Length && signature[index] != ')')
                    index = SkipCompleteType(signature, index, arrayDepth, structDepth + 1);
                if (index >= signature.Length)
                    throw new DBusProtocolException($"Signature '{signature}' has an unclosed struct.");
                return index + 1;
            }

            default:
                throw new DBusProtocolException($"Signature '{signature}' has an invalid type code '{code}'.");
        }
    }

    /// <summary>
    /// Returns the index just past an array's element type, which may be a dict entry.
    /// </summary>
    internal static int SkipElementType(string signature, int start) =>
        start < signature.Length && signature[start] == '{'
            ? SkipDictEntry(signature, start, 0, 0)
            : SkipCompleteType(signature, start, 0, 0);

    /// <summary>The alignment of a type, by its first character.</summary>
    internal static int Alignment(char code) => code switch
    {
        'y' or 'g' or 'v' => 1,
        'n' or 'q' => 2,
        'b' or 'i' or 'u' or 'h' or 's' or 'o' or 'a' => 4,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => throw new DBusProtocolException($"Invalid type code '{code}'."),
    };

    private static bool IsBasic(char code) =>
        code is 'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 's' or 'o' or 'g' or 'h';

    // A dict entry appears only straight after 'a', with a basic key and exactly one value type.
    private static int SkipDictEntry(string signature, int open, int arrayDepth, int structDepth)
    {
        if (structDepth + 1 > MaxNesting)
            throw new DBusProtocolException($"Signature '{signature}' nests structs deeper than {MaxNesting}.");

        int keyIndex = open + 1;
        if (keyIndex >= signature.Length || !IsBasic(signature[keyIndex]))
            throw new DBusProtocolException($"Signature '{signature}' has a dict entry without a basic key.");

        int valueEnd = SkipCompleteType(signature, keyIndex + 1, arrayDepth, structDepth + 1);
        if (valueEnd >= signature.Length || signature[valueEnd] != '}')
            throw new DBusProtocolException($"Signature '{signature}' has a dict entry that is not a key and one value.");
        return valueEnd + 1;
    }
}
