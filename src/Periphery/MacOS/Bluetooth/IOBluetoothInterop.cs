// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Periphery.MacOS.Bluetooth.Core;

namespace Periphery.MacOS.Bluetooth;

/// <summary>
/// IOBluetooth and CoreBluetooth through the Objective-C runtime (ADR-0093 D1). Each call sends one
/// message with a fixed prototype, and every read happens inside an autorelease pool.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class IOBluetoothInterop
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string IOBluetooth = "/System/Library/Frameworks/IOBluetooth.framework/IOBluetooth";
    private const string CoreBluetooth = "/System/Library/Frameworks/CoreBluetooth.framework/CoreBluetooth";

    // Loading the frameworks registers their classes with the runtime.
    private static readonly Lazy<bool> s_loaded = new(() =>
        NativeLibrary.TryLoad(IOBluetooth, out _) && NativeLibrary.TryLoad(CoreBluetooth, out _));

    private static readonly IntPtr s_authorization = sel_registerName("authorization");
    private static readonly IntPtr s_pairedDevices = sel_registerName("pairedDevices");
    private static readonly IntPtr s_count = sel_registerName("count");
    private static readonly IntPtr s_objectAtIndex = sel_registerName("objectAtIndex:");
    private static readonly IntPtr s_addressString = sel_registerName("addressString");
    private static readonly IntPtr s_name = sel_registerName("name");
    private static readonly IntPtr s_isConnected = sel_registerName("isConnected");
    private static readonly IntPtr s_classOfDevice = sel_registerName("classOfDevice");
    private static readonly IntPtr s_utf8String = sel_registerName("UTF8String");

    /// <summary>
    /// <c>+[CBManager authorization]</c>, which never prompts. <see langword="null"/> when the
    /// frameworks do not load.
    /// </summary>
    internal static BluetoothAuthorization? ReadAuthorization()
    {
        if (!s_loaded.Value)
            return null;
        IntPtr manager = objc_getClass("CBManager");
        if (manager == IntPtr.Zero)
            return null;
        return (BluetoothAuthorization)(long)SendNInt(manager, s_authorization);
    }

    /// <summary>
    /// <c>+[IOBluetoothDevice pairedDevices]</c>. <see langword="null"/> when the frameworks do not
    /// load. Without Bluetooth permission it returns no bonds and no error, so ask
    /// <see cref="ReadAuthorization"/> first.
    /// </summary>
    internal static ImmutableArray<IOBluetoothBond>? ReadBonds()
    {
        if (!s_loaded.Value)
            return null;
        IntPtr deviceClass = objc_getClass("IOBluetoothDevice");
        if (deviceClass == IntPtr.Zero)
            return null;

        IntPtr pool = objc_autoreleasePoolPush();
        try
        {
            IntPtr devices = Send(deviceClass, s_pairedDevices);
            if (devices == IntPtr.Zero)
                return [];

            nuint count = SendNUInt(devices, s_count);
            var bonds = ImmutableArray.CreateBuilder<IOBluetoothBond>();
            for (nuint i = 0; i < count; i++)
            {
                IntPtr device = SendIndex(devices, s_objectAtIndex, i);
                if (String(Send(device, s_addressString)) is not { } address)
                    continue;
                bonds.Add(new IOBluetoothBond(
                    address,
                    String(Send(device, s_name)),
                    SendByte(device, s_isConnected) != 0,
                    SendUInt(device, s_classOfDevice)));
            }
            return bonds.ToImmutable();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// The <c>DeviceAddress</c> and <c>Transport</c> of every IOKit HID node that has both, which are
    /// the Bluetooth HID devices connected now. Plain IOKit, so no Bluetooth permission is needed.
    /// </summary>
    internal static ImmutableArray<HidLink> ReadHidLinks()
    {
        IntPtr matching = IOKitInterop.IOServiceMatching(MacOSCategoryMap.IOHIDDevice);
        if (matching == IntPtr.Zero)
            return [];
        // IOServiceGetMatchingServices consumes the matching dictionary.
        if (IOKitInterop.IOServiceGetMatchingServices(IOKitInterop.kIOMasterPortDefault, matching, out uint iterator) != IOKitInterop.kIOReturnSuccess)
            return [];

        var links = ImmutableArray.CreateBuilder<HidLink>();
        try
        {
            uint service;
            while ((service = IOKitInterop.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    if (IOKitInterop.IORegistryEntryCreateCFProperties(service, out IntPtr properties, IntPtr.Zero, 0) != IOKitInterop.kIOReturnSuccess
                        || properties == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (IOKitInterop.GetCFStringValue(properties, "DeviceAddress") is { } address
                            && IOKitInterop.GetCFStringValue(properties, "Transport") is { } transport)
                        {
                            links.Add(new HidLink(address, transport));
                        }
                    }
                    finally
                    {
                        IOKitInterop.CFRelease(properties);
                    }
                }
                finally
                {
                    IOKitInterop.IOObjectRelease(service);
                }
            }
        }
        finally
        {
            IOKitInterop.IOObjectRelease(iterator);
        }
        return links.ToImmutable();
    }

    private static string? String(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, s_utf8String));

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial IntPtr objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(IntPtr pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendNInt(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nuint SendNUInt(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendByte(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial uint SendUInt(IntPtr receiver, IntPtr selector);
}
