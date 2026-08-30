// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Generic;

namespace Periphery.Hid;

/// <summary>
/// Summary of a HID report descriptor: the top-level application usage and
/// the maximum payload sizes per report type.
/// </summary>
/// <remarks>
/// On Windows the OS HID parser (<c>HidP_GetCaps</c>) supplies these values;
/// on Linux the kernel hands back the raw descriptor bytes
/// (<c>HIDIOCGRDESC</c>) and the library derives them itself via
/// <see cref="HidReportDescriptor.Parse"/>. Payload lengths exclude the
/// report-ID byte, matching the cross-platform
/// <see cref="HidDevice.MaxInputReportLength"/> contract.
/// </remarks>
internal readonly record struct HidReportDescriptorInfo(
    ushort UsagePage,
    ushort Usage,
    bool UsesReportIds,
    int MaxInputPayloadBytes,
    int MaxOutputPayloadBytes,
    int MaxFeaturePayloadBytes);

/// <summary>
/// Minimal, pure HID report-descriptor parser. Walks the item stream just far
/// enough to recover what the transfer surface needs: the first top-level
/// application collection's usage page/usage, whether the device uses
/// numbered reports, and the largest input/output/feature report payloads.
/// </summary>
/// <remarks>
/// Deliberately not a full descriptor model (no usage ranges, no value
/// scaling, no collection tree) — see ADR-0020 NEG-004. Real-world
/// descriptors are sometimes sloppy, so the parser is best-effort: malformed
/// or truncated input terminates the walk and yields whatever was
/// accumulated, never an exception.
/// </remarks>
internal static class HidReportDescriptor
{
    // Item type (bits 2-3 of the prefix byte).
    private const int TypeMain = 0;
    private const int TypeGlobal = 1;
    private const int TypeLocal = 2;

    // Main item tags.
    private const int TagInput = 0x8;
    private const int TagOutput = 0x9;
    private const int TagCollection = 0xA;
    private const int TagFeature = 0xB;
    private const int TagEndCollection = 0xC;

    // Global item tags.
    private const int TagUsagePage = 0x0;
    private const int TagReportSize = 0x7;
    private const int TagReportId = 0x8;
    private const int TagReportCount = 0x9;
    private const int TagPush = 0xA;
    private const int TagPop = 0xB;

    // Local item tags.
    private const int TagUsage = 0x0;

    private const int CollectionApplication = 0x01;

    public static HidReportDescriptorInfo Parse(ReadOnlySpan<byte> descriptor)
    {
        // Global state (subject to Push/Pop).
        var globals = new GlobalState();
        var globalStack = new Stack<GlobalState>();

        // Local state (reset after every Main item).
        uint? pendingUsage = null;
        ushort? pendingUsagePageOverride = null; // From 32-bit extended usages.

        // Accumulators.
        var inputBits = new Dictionary<byte, long>();
        var outputBits = new Dictionary<byte, long>();
        var featureBits = new Dictionary<byte, long>();
        bool usesReportIds = false;
        int collectionDepth = 0;
        ushort appUsagePage = 0;
        ushort appUsage = 0;
        bool appCaptured = false;

        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i++];

            // Long item (0xFE): tag byte + size byte + payload; nothing in a
            // long item affects the values this parser recovers, so skip it.
            if (prefix == 0xFE)
            {
                if (i >= descriptor.Length) break;
                int longSize = descriptor[i];
                i += 2 + longSize; // size byte + tag byte + payload
                continue;
            }

            int dataSize = (prefix & 0x3) switch { 3 => 4, var n => n };
            int type = (prefix >> 2) & 0x3;
            int tag = (prefix >> 4) & 0xF;

            if (i + dataSize > descriptor.Length)
                break; // Truncated descriptor — stop with what we have.

            uint data = 0;
            for (int b = 0; b < dataSize; b++)
                data |= (uint)descriptor[i + b] << (8 * b);
            i += dataSize;

            switch (type)
            {
                case TypeGlobal:
                    switch (tag)
                    {
                        case TagUsagePage:
                            globals.UsagePage = (ushort)data;
                            break;
                        case TagReportSize:
                            globals.ReportSize = (int)data;
                            break;
                        case TagReportCount:
                            globals.ReportCount = (int)data;
                            break;
                        case TagReportId:
                            globals.ReportId = (byte)data;
                            usesReportIds = true;
                            break;
                        case TagPush:
                            globalStack.Push(globals);
                            break;
                        case TagPop:
                            if (globalStack.Count > 0)
                                globals = globalStack.Pop();
                            break;
                    }
                    break;

                case TypeLocal:
                    if (tag == TagUsage && pendingUsage is null)
                    {
                        // Only the first usage matters for the application
                        // collection capture. A 4-byte usage is "extended":
                        // high 16 bits carry an inline usage-page override.
                        pendingUsage = data & 0xFFFF;
                        if (dataSize == 4)
                            pendingUsagePageOverride = (ushort)(data >> 16);
                    }
                    break;

                case TypeMain:
                    switch (tag)
                    {
                        case TagCollection:
                            if (!appCaptured && collectionDepth == 0 && data == CollectionApplication)
                            {
                                appUsagePage = pendingUsagePageOverride ?? globals.UsagePage;
                                appUsage = (ushort)(pendingUsage ?? 0);
                                appCaptured = true;
                            }
                            collectionDepth++;
                            break;
                        case TagEndCollection:
                            if (collectionDepth > 0) collectionDepth--;
                            break;
                        case TagInput:
                            Accumulate(inputBits, globals);
                            break;
                        case TagOutput:
                            Accumulate(outputBits, globals);
                            break;
                        case TagFeature:
                            Accumulate(featureBits, globals);
                            break;
                    }
                    // Local items do not carry past a Main item.
                    pendingUsage = null;
                    pendingUsagePageOverride = null;
                    break;
            }
        }

        return new HidReportDescriptorInfo(
            appUsagePage,
            appUsage,
            usesReportIds,
            MaxPayloadBytes(inputBits),
            MaxPayloadBytes(outputBits),
            MaxPayloadBytes(featureBits));
    }

    private static void Accumulate(Dictionary<byte, long> bits, in GlobalState globals)
    {
        long add = (long)globals.ReportSize * globals.ReportCount;
        if (add <= 0) return;
        bits.TryGetValue(globals.ReportId, out long current);
        bits[globals.ReportId] = current + add;
    }

    private static int MaxPayloadBytes(Dictionary<byte, long> bits)
    {
        long max = 0;
        foreach (long b in bits.Values)
            if (b > max) max = b;
        // Round bits up to whole bytes; clamp to a sane ceiling so a garbage
        // descriptor can't drive huge buffer allocations downstream.
        long bytes = (max + 7) / 8;
        return (int)Math.Min(bytes, 0x10000);
    }

    private struct GlobalState
    {
        public ushort UsagePage;
        public int ReportSize;
        public int ReportCount;
        public byte ReportId;
    }
}
