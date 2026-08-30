// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.MacOS;

/// <summary>
/// P/Invoke declarations for IOKit.framework, CoreFoundation.framework, and libdispatch.
/// All declarations use <see cref="LibraryImportAttribute"/> for AOT/trim safety on Apple Silicon.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class IOKitInterop
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // ── IOKit constants ────────────────────────────────────────────────

    internal const uint kIOMasterPortDefault = 0;
    internal const uint kIORegistryIterateRecursively = 0x00000001;
    internal const uint kCFStringEncodingUTF8 = 0x08000100;
    internal const int kIOReturnSuccess = 0;

    // IOKit notification types (C string constants)
    internal const string kIOMatchedNotification = "IOServiceMatched";
    internal const string kIOTerminatedNotification = "IOServiceTerminate";
    internal const string kIOGeneralInterest = "IOGeneralInterest";

    // IOKit message types for interest notifications
    internal const uint kIOMessageServicePropertyChange = 0xe0000110;

    // CoreFoundation type IDs
    internal const uint kCFNumberSInt32Type = 3;
    internal const uint kCFNumberSInt64Type = 4;

    // ── IOKit enumeration ──────────────────────────────────────────────

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    internal static partial int IOServiceGetMatchingServices(
        uint masterPort, IntPtr matchingDict, out uint iterator);

    [LibraryImport(IOKit)]
    internal static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IOIteratorIsValid(uint iterator);

    [LibraryImport(IOKit)]
    internal static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKit)]
    internal static partial int IOObjectRetain(uint obj);

    // ── IOKit registry entry ───────────────────────────────────────────

    [LibraryImport(IOKit)]
    internal static partial int IORegistryEntryGetRegistryEntryID(
        uint entry, out ulong entryID);

    [LibraryImport(IOKit)]
    internal static partial int IORegistryEntryCreateCFProperties(
        uint entry, out IntPtr properties, IntPtr allocator, uint options);

    [LibraryImport(IOKit)]
    internal static partial IntPtr IORegistryEntryCreateCFProperty(
        uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IORegistryEntryGetNameInPlane(
        uint entry, string plane, IntPtr name);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint IORegistryEntryFromPath(
        uint masterPort, string path);

    // ── IOKit class name ───────────────────────────────────────────────

    /// <summary>
    /// Retrieves the IOKit class name of an I/O Registry entry.
    /// Buffer must be at least 128 bytes.
    /// </summary>
    [LibraryImport(IOKit)]
    internal static partial int IOObjectGetClass(uint obj, IntPtr className);

    // ── IOKit notification port ────────────────────────────────────────

    [LibraryImport(IOKit)]
    internal static partial IntPtr IONotificationPortCreate(uint masterPort);

    [LibraryImport(IOKit)]
    internal static partial void IONotificationPortDestroy(IntPtr notify);

    [LibraryImport(IOKit)]
    internal static partial void IONotificationPortSetDispatchQueue(
        IntPtr notify, IntPtr queue);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IOServiceAddMatchingNotification(
        IntPtr notifyPort,
        string notificationType,
        IntPtr matchingDict,
        delegate* unmanaged[Cdecl]<IntPtr, uint, void> callback,
        IntPtr refCon,
        out uint notification);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int IOServiceAddInterestNotification(
        IntPtr notifyPort,
        uint service,
        string interestType,
        delegate* unmanaged[Cdecl]<IntPtr, uint, uint, IntPtr, void> callback,
        IntPtr refCon,
        out uint notification);

    // ── CoreFoundation ─────────────────────────────────────────────────

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr CFStringCreateWithCString(
        IntPtr alloc, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRetain(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFDataGetTypeID();

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFBooleanGetTypeID();

    [LibraryImport(CoreFoundation)]
    internal static partial uint CFDictionaryGetTypeID();

    // CFString
    [LibraryImport(CoreFoundation)]
    internal static partial int CFStringGetLength(IntPtr theString);

    [LibraryImport(CoreFoundation)]
    internal static partial int CFStringGetMaximumSizeForEncoding(
        int length, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFStringGetCString(
        IntPtr theString, IntPtr buffer, int bufferSize, uint encoding);

    // CFNumber
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFNumberGetValue(
        IntPtr number, uint theType, out int value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFNumberGetValue(
        IntPtr number, uint theType, out long value);

    // CFData
    [LibraryImport(CoreFoundation)]
    internal static partial int CFDataGetLength(IntPtr theData);

    [LibraryImport(CoreFoundation)]
    internal static partial IntPtr CFDataGetBytePtr(IntPtr theData);

    // CFBoolean
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CFBooleanGetValue(IntPtr boolean);

    // CFDictionary
    [LibraryImport(CoreFoundation)]
    internal static partial IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [LibraryImport(CoreFoundation)]
    internal static partial int CFDictionaryGetCount(IntPtr theDict);

    // ── libdispatch (GCD) ──────────────────────────────────────────────

    [LibraryImport(LibSystem)]
    internal static partial IntPtr dispatch_get_global_queue(nint identifier, nuint flags);

    // ── BSD / POSIX (getifaddrs) ───────────────────────────────────────

    [LibraryImport(LibSystem)]
    internal static partial int getifaddrs(out IntPtr ifap);

    [LibraryImport(LibSystem)]
    internal static partial void freeifaddrs(IntPtr ifap);

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a CFString key, looks up the value in a CFDictionary, and reads it as a managed string.
    /// Returns <c>null</c> if the key is missing or the value is not a CFString.
    /// The caller must release <paramref name="properties"/> separately.
    /// </summary>
    internal static string? GetCFStringValue(IntPtr properties, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero) return null;
            if (CFGetTypeID(value) != CFStringGetTypeID()) return null;
            return CFStringToManaged(value);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>
    /// Creates a CFString key, looks up the value in a CFDictionary, and reads it as an int.
    /// Returns <c>null</c> if the key is missing or the value is not a CFNumber.
    /// </summary>
    internal static int? GetCFNumberIntValue(IntPtr properties, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero) return null;
            if (CFGetTypeID(value) != CFNumberGetTypeID()) return null;
            if (CFNumberGetValue(value, kCFNumberSInt32Type, out int result))
                return result;
            return null;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>
    /// Creates a CFString key, looks up the value in a CFDictionary, and reads it as a long.
    /// Returns <c>null</c> if the key is missing or the value is not a CFNumber.
    /// </summary>
    internal static long? GetCFNumberLongValue(IntPtr properties, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero) return null;
            if (CFGetTypeID(value) != CFNumberGetTypeID()) return null;
            if (CFNumberGetValue(value, kCFNumberSInt64Type, out long result))
                return result;
            return null;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>
    /// Creates a CFString key, looks up the value in a CFDictionary, and reads it as a boolean.
    /// Returns <c>null</c> if the key is missing or the value is not a CFBoolean.
    /// </summary>
    internal static bool? GetCFBooleanValue(IntPtr properties, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero) return null;
            if (CFGetTypeID(value) != CFBooleanGetTypeID()) return null;
            return CFBooleanGetValue(value);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>
    /// Creates a CFString key, looks up the value in a CFDictionary, and reads it as a byte array.
    /// Returns <c>null</c> if the key is missing or the value is not CFData.
    /// </summary>
    internal static byte[]? GetCFDataValue(IntPtr properties, string key)
    {
        IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
        if (cfKey == IntPtr.Zero) return null;
        try
        {
            IntPtr value = CFDictionaryGetValue(properties, cfKey);
            if (value == IntPtr.Zero) return null;
            if (CFGetTypeID(value) != CFDataGetTypeID()) return null;

            int length = CFDataGetLength(value);
            if (length <= 0) return null;

            IntPtr ptr = CFDataGetBytePtr(value);
            byte[] data = new byte[length];
            Marshal.Copy(ptr, data, 0, length);
            return data;
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>
    /// Converts a CFString to a managed string. Returns <c>null</c> if conversion fails.
    /// </summary>
    internal static string? CFStringToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;

        int length = CFStringGetLength(cfString);
        if (length == 0) return string.Empty;

        int bufferSize = CFStringGetMaximumSizeForEncoding(length, kCFStringEncodingUTF8) + 1;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (CFStringGetCString(cfString, buffer, bufferSize, kCFStringEncodingUTF8))
                return Marshal.PtrToStringUTF8(buffer);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the IOKit class name from an <c>io_object_t</c>.
    /// Returns <c>null</c> if the call fails.
    /// </summary>
    internal static string? GetIOObjectClassName(uint obj)
    {
        // IOKit class names are max 128 bytes
        IntPtr buffer = Marshal.AllocHGlobal(128);
        try
        {
            int kr = IOObjectGetClass(obj, buffer);
            if (kr != kIOReturnSuccess) return null;
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Probes whether IOKit.framework is loadable at runtime.
    /// Returns <c>true</c> if <c>IOKit.framework/IOKit</c> can be loaded; <c>false</c> otherwise.
    /// </summary>
    internal static bool IsIOKitAvailable()
    {
        return NativeLibrary.TryLoad(IOKit, out _);
    }
}
