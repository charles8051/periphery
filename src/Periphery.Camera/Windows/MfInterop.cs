// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Periphery.Camera.Windows;

// ════════════════════════════════════════════════════════════════════════════
// Media Foundation interop: source-generated COM (GeneratedComInterface) plus
// LibraryImport P/Invokes. AOT- and trim-friendly per
// https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation
//
// ⚠  STOP — READ THIS BEFORE EDITING:
//    docs/patterns/source-generated-com-interop.md
//
// Two non-obvious failure modes have already cost the team a debugging session
// on this file:
//
//   1. Missing vtable slots. Method declaration order MUST match the native
//      vtable — every native slot between IUnknown and the last method we
//      use has to be declared, in order, with the right signature.
//      Source-gen does not honor _VtblGap*, and an off-by-one shifts every
//      later slot, dispatching calls into the wrong native function.
//
//   2. Wrong IID. Source-gen casts (e.g. `(IMFSample)x`) route through
//      QueryInterface(IID_T). If the IID in your [Guid] attribute doesn't
//      match the canonical SDK header, the QI returns E_NOINTERFACE on
//      every COM object and the cast throws InvalidCastException. The
//      diagnostic LOOKS like the COM object is broken; it's not.
//      ALWAYS verify IIDs against win32metadata's mfobjects.h / dshow.h /
//      etc., not against natural-language docs.
//
// All interfaces are partial and internal so the generator can emit per-method
// vtable trampolines. Verify slot numbers after every edit:
//
//   dotnet build … -p:EmitCompilerGeneratedFiles=true
//   grep -E "Vtable\.[A-Za-z]+_[0-9]+" obj/Debug/<tfm>/generated/.../<Iface>.cs
//
// Slot N in the generator output must equal slot N in the SDK header.
// ════════════════════════════════════════════════════════════════════════════

[SupportedOSPlatform("windows")]
internal static partial class MfInterop
{
    // ── ComWrappers strategy (single instance shared by all backends) ──────

    internal static readonly StrategyBasedComWrappers Wrappers = new();

    // ── P/Invoke: mfplat.dll ───────────────────────────────────────────────

    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint dwFlags);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateMediaType(out IMFMediaType ppMFType);

    internal const uint MF_VERSION = 0x00020070; // SDK 2.0, API 0x70
    internal const uint MFSTARTUP_NOSOCKET = 0x1;

    // ── P/Invoke: mf.dll ───────────────────────────────────────────────────
    //
    // MFEnumDeviceSources output is an MF-allocated array (CoTaskMemAlloc) of
    // IMFActivate*. The ABI is `IMFActivate***` so we accept a raw pointer and
    // walk it with ComWrappers, then CoTaskMemFree the buffer.

    [LibraryImport("mf.dll")]
    internal static partial int MFEnumDeviceSources(
        IMFAttributes pAttributes,
        out nint pppSourceActivate,
        out uint pcSourceActivate);

    // ── P/Invoke: mfreadwrite.dll ──────────────────────────────────────────

    [LibraryImport("mfreadwrite.dll")]
    internal static partial int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource pMediaSource,
        IMFAttributes? pAttributes,
        out IMFSourceReader ppSourceReader);

    // ── HRESULT codes ──────────────────────────────────────────────────────

    internal const int S_OK = 0;
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    internal const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36BD);
    internal const int MF_E_INVALIDREQUEST = unchecked((int)0xC00D36B2);
    internal const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3704);
    internal const int MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED = unchecked((int)0xC00D3EA2);
    internal const int MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED = unchecked((int)0xC00D3EA3);
    internal const int E_ACCESSDENIED = unchecked((int)0x80070005);
    internal const int E_NOT_FOUND = unchecked((int)0x80070490);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);

    // Source reader stream flags
    internal const uint MF_SOURCE_READERF_ERROR = 0x00000001;
    internal const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    internal const uint MF_SOURCE_READERF_NEWSTREAM = 0x00000004;
    internal const uint MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED = 0x00000010;
    internal const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x00000020;
    internal const uint MF_SOURCE_READERF_STREAMTICK = 0x00000100;

    internal const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    internal const uint MF_SOURCE_READER_ANY_STREAM = 0xFFFFFFFE;

    // Camera/proc-amp control flags
    /// <summary>
    /// The driver does not implement this property. Distinct from a real
    /// failure: it means "no such control", not "the read went wrong".
    /// </summary>
    internal const int E_PROP_ID_UNSUPPORTED = unchecked((int)0x80070490);

    internal const int MF_CAMERA_FLAGS_AUTO = 0x0001;
    internal const int MF_CAMERA_FLAGS_MANUAL = 0x0002;

    // ── IIDs (avoid reflection via typeof(...).GUID for AOT safety) ────────

    internal static readonly Guid IID_IMFMediaSource = new("279A808D-AEC7-40C8-9C6B-A6B492C78A66");
    internal static readonly Guid IID_IAMCameraControl = new("C6E13370-30AC-11D0-A18C-00A0C9118956");
    internal static readonly Guid IID_IAMVideoProcAmp = new("C6E13360-30AC-11D0-A18C-00A0C9118956");

    // ── Device enumeration GUIDs ───────────────────────────────────────────

    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE =
        new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");

    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
        new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");

    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK =
        new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");

    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME =
        new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");

    // ── Media type attribute GUIDs ─────────────────────────────────────────

    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    internal static readonly Guid MF_MT_SUBTYPE = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652C33D-D6B2-4012-B834-72030849A37D");
    internal static readonly Guid MF_MT_FRAME_RATE = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    internal static readonly Guid MF_MT_FRAME_RATE_RANGE_MIN = new("D2E7558C-DC1F-403F-9A72-D28BB1EB3B5E");
    internal static readonly Guid MF_MT_FRAME_RATE_RANGE_MAX = new("E3371D41-B4CF-4A05-BD4E-20B88BB2C4D6");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    internal static readonly Guid MF_MT_SAMPLE_SIZE = new("DAD3AB78-1990-408B-BCE2-EBA673DACC10");
    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");

    // ── Video format subtype GUIDs ─────────────────────────────────────────

    internal static readonly Guid MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_UYVY = new("59565955-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_NV21 = new("3132564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_I420 = new("30323449-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_YV12 = new("32315659-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_MJPG = new("47504A4D-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_RGB24 = new("00000014-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_L8 = new("00000032-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MFVideoFormat_L16 = new("00000051-0000-0010-8000-00AA00389B71");

    // ── Source reader attribute GUIDs ──────────────────────────────────────

    internal static readonly Guid MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN =
        new("56B67165-219E-456D-A22E-2D3004C7FE56");

    internal static readonly Guid MF_READWRITE_DISABLE_CONVERTERS =
        new("98D5B065-1374-4847-8D5D-31520FEE7156");


    // ── Lifetime helpers ───────────────────────────────────────────────────
    //
    // Source-generated COM RCWs are ComObject instances. FinalRelease() drops
    // every cached IUnknown reference held by the wrapper. Use it where the
    // legacy code called Marshal.ReleaseComObject — those Marshal APIs warn
    // SYSLIB1099 against source-generated COM and may throw at runtime.

    internal static void Release<T>(ref T? wrapper) where T : class
    {
        if (wrapper is ComObject co)
        {
            co.FinalRelease();
        }
        wrapper = null;
    }

    // ── QI diagnostic ─────────────────────────────────────────────────────

    /// <summary>
    /// Drop this in temporarily when a source-gen COM cast throws
    /// <see cref="InvalidCastException"/>. Calls <c>QueryInterface</c> directly
    /// on the raw pointer (bypassing source-gen wrapping) and prints the HR
    /// to stderr. Returns the HR so callers can branch on it.
    /// <para>
    /// 0 means QI works — your cast failure is somewhere else (most often a
    /// wrong IID; double-check it against the canonical SDK header).
    /// 0x80004002 (E_NOINTERFACE) means the object genuinely doesn't
    /// implement the IID (or you have the wrong IID).
    /// </para>
    /// </summary>
    internal static int ProbeQi(nint comPtr, in Guid iid, string label)
    {
        if (comPtr == 0)
        {
            Console.Error.WriteLine($"QI({label}) ptr=NULL");
            return unchecked((int)0x80004003); // E_POINTER
        }
        int hr = Marshal.QueryInterface(comPtr, in iid, out var p);
        if (p != 0) Marshal.Release(p);
        string note = hr switch
        {
            0 => "S_OK",
            unchecked((int)0x80004002) => "E_NOINTERFACE — verify the IID against the canonical SDK header",
            _ => "unexpected",
        };
        Console.Error.WriteLine($"QI({label}, {iid:B}) ptr=0x{comPtr:X16} hr=0x{hr:X8} ({note})");
        return hr;
    }

    /// <summary>
    /// Calls IMFAttributes::GetAllocatedString and converts the CoTaskMem-allocated
    /// wide string to a managed string, freeing the native buffer afterwards.
    /// Returns the HRESULT; <paramref name="value"/> is empty on failure.
    /// </summary>
    internal static int GetAllocatedString(IMFAttributes attrs, in Guid key, out string value)
    {
        int hr = attrs.GetAllocatedString(key, out nint ptr, out _);
        if (hr < 0 || ptr == 0)
        {
            value = string.Empty;
            return hr;
        }
        try
        {
            value = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            return hr;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ── 64-bit packed attribute helpers ────────────────────────────────────

    /// <summary>
    /// Unpacks a 64-bit value into two 32-bit components, used for
    /// MF_MT_FRAME_SIZE (width/height) and MF_MT_FRAME_RATE (num/denom).
    /// </summary>
    internal static void Unpack2xUInt32(ulong packed, out uint hi, out uint lo)
    {
        hi = (uint)(packed >> 32);
        lo = (uint)(packed & 0xFFFFFFFF);
    }

    internal static ulong Pack2xUInt32(uint hi, uint lo) => ((ulong)hi << 32) | lo;
}

// ════════════════════════════════════════════════════════════════════════════
// IMFAttributes — base for all MF property bags. StringMarshalling.Utf16 makes
// `string` and `out string` parameters wide-string with CoTaskMemFree on the
// allocated-string return path (matches GetAllocatedString contract).
// ════════════════════════════════════════════════════════════════════════════

// Note: do NOT set StringMarshalling on IMFAttributes. With Utf16, the source
// generator inserts a phantom GetStringLength slot ahead of GetString, which
// shifts the entire vtable by one off the native MF layout and corrupts every
// call past slot 10. Keep all string parameters as nint and convert manually
// via PtrToStringUni / FreeCoTaskMem.
[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
internal partial interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, nint pValue);
    [PreserveSig] int GetItemType(in Guid guidKey, out uint pType);
    [PreserveSig] int CompareItem(in Guid guidKey, nint Value, [MarshalAs(UnmanagedType.U4)] out bool pbResult);
    [PreserveSig] int Compare(IMFAttributes pTheirs, uint MatchType, [MarshalAs(UnmanagedType.U4)] out bool pbResult);
    [PreserveSig] int GetUINT32(in Guid guidKey, out uint punValue);
    [PreserveSig] int GetUINT64(in Guid guidKey, out ulong punValue);
    [PreserveSig] int GetDouble(in Guid guidKey, out double pfValue);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid pguidValue);
    // Native IMFAttributes has GetStringLength at slot 11 between GetGUID and
    // GetString. The original [ComImport] declaration omitted it (and skirted
    // the bug because GetString was never called); source-gen is strict, so
    // omitting it shifts every later method by one slot — SetGUID would land
    // on native SetDouble's slot and crash on the double/pointer ABI mismatch.
    [PreserveSig] int GetStringLength(in Guid guidKey, out uint pcchLength);
    [PreserveSig] int GetString(in Guid guidKey, nint pwszValue, uint cchBufSize, out uint pcchLength);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out nint ppwszValue, out uint pcchLength);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out uint pcbBlobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, nint pBuf, uint cbBufSize, out uint pcbBlobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out nint ppBuf, out uint pcbSize);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, out nint ppv);
    [PreserveSig] int SetItem(in Guid guidKey, nint Value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, uint unValue);
    [PreserveSig] int SetUINT64(in Guid guidKey, ulong unValue);
    [PreserveSig] int SetDouble(in Guid guidKey, double fValue);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid guidValue);
    [PreserveSig] int SetString(in Guid guidKey, nint wszValue);
    [PreserveSig] int SetBlob(in Guid guidKey, nint pBuf, uint cbBufSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, nint pUnknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint pcItems);
    [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, nint pValue);
    [PreserveSig] int CopyAllItems(IMFAttributes pDest);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
internal partial interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid pguidMajorType);
    [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.U4)] out bool pfCompressed);
    [PreserveSig] int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    [PreserveSig] int GetRepresentation(Guid guidRepresentation, out nint ppvRepresentation);
    [PreserveSig] int FreeRepresentation(Guid guidRepresentation, nint pvRepresentation);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
internal partial interface IMFActivate : IMFAttributes
{
    // ActivateObject returns IUnknown for an arbitrary IID. We always request
    // IID_IMFMediaSource so we type the out parameter directly — the source
    // generator wires up ComInterfaceMarshaller<IMFMediaSource> for us.
    [PreserveSig] int ActivateObject(in Guid riid, out IMFMediaSource ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

// IMFMediaSource extends IMFMediaEventGenerator; we inline the 4 base slots
// rather than declaring a separate interface since we don't call them.
[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66")]
internal partial interface IMFMediaSource
{
    // IMFMediaEventGenerator (slots 3–6)
    [PreserveSig] int GetEvent(uint dwFlags, out nint ppEvent);
    [PreserveSig] int BeginGetEvent(nint pCallback, nint punkState);
    [PreserveSig] int EndGetEvent(nint pResult, out nint ppEvent);
    [PreserveSig] int QueueEvent(uint met, in Guid guidExtendedType, int hrStatus, nint pvValue);

    // IMFMediaSource (slots 7–12)
    [PreserveSig] int GetCharacteristics(out uint pdwCharacteristics);
    [PreserveSig] int CreatePresentationDescriptor(out nint ppPresentationDescriptor);
    [PreserveSig] int Start(nint pPresentationDescriptor, nint pguidTimeFormat, nint pvarStartPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993")]
internal partial interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.U4)] out bool pfSelected);
    [PreserveSig] int SetStreamSelection(uint dwStreamIndex, [MarshalAs(UnmanagedType.U4)] bool fSelected);
    [PreserveSig] int GetNativeMediaType(uint dwStreamIndex, uint dwMediaTypeIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int GetCurrentMediaType(uint dwStreamIndex, out IMFMediaType ppMediaType);
    [PreserveSig] int SetCurrentMediaType(uint dwStreamIndex, nint pdwReserved, IMFMediaType pMediaType);
    // Native vtable has SetCurrentPosition between SetCurrentMediaType and
    // ReadSample (slot 8). We never call it, but the slot must exist or
    // ReadSample lands on SetCurrentPosition's slot and access-violates
    // when it dereferences our DWORD as a REFGUID*. Same class of bug as
    // the missing IMFAttributes::GetStringLength slot — see
    // docs/patterns/source-generated-com-interop.md.
    [PreserveSig] int SetCurrentPosition(in Guid guidTimeFormat, nint pvarPosition);
    // ReadSample's ppSample is returned as a raw COM pointer the caller wraps
    // explicitly. The default ComInterfaceMarshaller<IMFSample> path uses
    // shared (non-unique) wrappers with the Unwrap flag, which appears to
    // accumulate a permanent extra ref per call on this backend (the Mf
    // source-reader internal sample pool exhausts after ~20 reads on every
    // camera tested, even after FinalRelease). Explicit nint + UniqueInstance
    // + FinalRelease per frame is reliable.
    [PreserveSig] int ReadSample(
        uint dwStreamIndex,
        uint dwControlFlags,
        out uint pdwActualStreamIndex,
        out uint pdwStreamFlags,
        out long pllTimestamp,
        out nint ppSample);
    [PreserveSig] int Flush(uint dwStreamIndex);
    [PreserveSig] int GetServiceForStream(uint dwStreamIndex, in Guid guidService, in Guid riid, out nint ppvObject);
    [PreserveSig] int GetPresentationAttribute(uint dwStreamIndex, in Guid guidAttribute, nint pvarAttribute);
}

// IIDs verified against the canonical Windows SDK header
// (https://github.com/microsoft/win32metadata/.../mfobjects.h). Do not copy IIDs
// from natural-language docs — verify against the header. See
// docs/patterns/source-generated-com-interop.md Hazard A.

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
internal partial interface IMFSample : IMFAttributes
{
    [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
    [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
    [PreserveSig] int GetSampleTime(out long phnsSampleTime);
    [PreserveSig] int SetSampleTime(long hnsSampleTime);
    [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
    [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
    [PreserveSig] int GetBufferByIndex(uint dwIndex, out nint ppBuffer);
    // Returns a raw IMFMediaBuffer* — same reasoning as ReadSample's
    // out nint ppSample (above): the default ComInterfaceMarshaller path
    // leaks an extra ref per call on this backend, exhausting MF's
    // internal sample/buffer pool after ~20 frames.
    [PreserveSig] int ConvertToContiguousBuffer(out nint ppBuffer);
    [PreserveSig] int AddBuffer(nint pBuffer);
    [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
    [PreserveSig] int CopyToBuffer(nint pBuffer);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
internal partial interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out nint ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB")]
internal partial interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out nint ppbScanline0, out int plPitch);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out nint pbScanline0, out int plPitch);
    [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.U4)] out bool pfIsContiguous);
    [PreserveSig] int GetContiguousLength(out uint pcbLength);
    [PreserveSig] int ContiguousCopyTo(nint pbDestBuffer, uint cbDestBuffer);
    [PreserveSig] int ContiguousCopyFrom(nint pbSrcBuffer, uint cbSrcBuffer);
}

// ── DirectShow camera control interfaces (queried from MF source via QI) ──

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("C6E13370-30AC-11D0-A18C-00A0C9118956")]
internal partial interface IAMCameraControl
{
    [PreserveSig] int GetRange(int property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
    [PreserveSig] int Set(int property, int lValue, int flags);
    [PreserveSig] int Get(int property, out int lValue, out int flags);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface]
[Guid("C6E13360-30AC-11D0-A18C-00A0C9118956")]
internal partial interface IAMVideoProcAmp
{
    [PreserveSig] int GetRange(int property, out int pMin, out int pMax, out int pSteppingDelta, out int pDefault, out int pCapsFlags);
    [PreserveSig] int Set(int property, int lValue, int flags);
    [PreserveSig] int Get(int property, out int lValue, out int flags);
}
