// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Globalization;
using System.IO;
using System.Reflection;
using Periphery.Bootloader.Efm8.Usb;

namespace Periphery.Treehopper.Control.Cli;

/// <summary>
/// Resolves the firmware image (build-embedded or <c>--file</c>) and the target version.
/// Migrated from the retired standalone updater (feature-spec ADR Decision 3); the
/// embedded-image build mechanism and precedence rules are unchanged.
/// </summary>
internal static class FirmwareSource
{
    private const string EmbeddedResourceName = "firmware.tfi";
    private const string VersionMetadataKey = "TreehopperFirmwareVersion";

    /// <summary>
    /// Target firmware version (raw bcdDevice code), or null. Precedence: explicit
    /// <paramref name="explicitTarget"/>; else, when using the embedded image, the version
    /// baked in at build time. With <c>--file</c> and no explicit target it is unknown.
    /// </summary>
    public static int? ResolveTargetVersion(string? filePath, int? explicitTarget)
    {
        if (explicitTarget is not null) return explicitTarget;
        return filePath is null ? EmbeddedVersion() : null;
    }

    /// <summary>
    /// Loads the firmware image (<c>--file</c> if given, otherwise the build-embedded
    /// image) and resolves it to a ready-to-flash boot-record stream. The format is
    /// inferred from the file extension and <b>verified against the content</b>: a
    /// <c>.hex</c> Intel HEX image is converted to boot records in-process, a
    /// <c>.tfi</c>/<c>.efm8</c> stream is validated and passed through, and a file whose
    /// content does not match its extension is refused (returned as <c>Error</c>) — so a
    /// wrong file is rejected up front, before any board in a fleet flash is touched.
    /// <c>Bytes</c> is always a boot-record stream on success.
    /// </summary>
    public static (byte[]? Bytes, string Origin, string? Error) ResolveImage(string? filePath)
    {
        byte[] raw;
        string origin;
        string fileName;

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
                return (null, filePath, $"Firmware file not found: {filePath}");
            try { raw = File.ReadAllBytes(filePath); }
            catch (IOException ex) { return (null, filePath, $"Could not read {filePath}: {ex.Message}"); }
            origin = filePath;
            fileName = Path.GetFileName(filePath);
        }
        else
        {
            byte[]? embedded = ReadEmbeddedImage();
            if (embedded is null)
                return (null, "(none)",
                    "No firmware image available. This build has no embedded image — pass --file <path>, "
                    + "or produce a fleet build with -p:TreehopperFirmwareImage=<path> -p:TreehopperFirmwareVersion=<code>.");
            raw = embedded;
            origin = "embedded image";
            fileName = EmbeddedResourceName;   // ".tfi" -> verified as a boot-record stream
        }

        // Infer + verify the format and convert (.hex -> boot records). A mismatched or
        // malformed file is refused here, before any board is touched.
        try
        {
            byte[] records = Efm8FirmwareImage.ToBootRecords(raw, fileName, Efm8BootOptions.Ub1);
            return (records, origin, null);
        }
        catch (Efm8BootFormatException ex)
        {
            return (null, origin, ex.Message);
        }
    }

    public static int? EmbeddedVersion()
    {
        foreach (var meta in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
            if (string.Equals(meta.Key, VersionMetadataKey, System.StringComparison.Ordinal)
                && int.TryParse(meta.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
        return null;
    }

    private static byte[]? ReadEmbeddedImage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
