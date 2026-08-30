// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Periphery.Windows;

/// <summary>
/// Minimal P/Invoke surface for a hidden top-level window and its message pump,
/// used by <see cref="WindowsDisplayChangeSink"/> to observe
/// <c>WM_DISPLAYCHANGE</c> broadcasts.
///
/// <para><b>Why a hidden top-level window and not a message-only window:</b>
/// <c>WM_DISPLAYCHANGE</c> is a system <i>broadcast</i>, and message-only
/// (<c>HWND_MESSAGE</c>-parented) windows are explicitly excluded from
/// broadcasts. The window must therefore be a real top-level window
/// (no parent) that is simply never shown. It is also delivered by
/// <c>SendMessage</c>, not posted, so it surfaces at the window procedure
/// during message-queue draining — the pump handles it in the <c>WndProc</c>,
/// not by inspecting the value <c>GetMessage</c> returns.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowMessageInterop
{
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_QUIT          = 0x0012;

    // Application-private message the WndProc posts to its own pump when a
    // WM_DISPLAYCHANGE arrives, so the (potentially slow) enrich work runs on the
    // pump loop rather than inline in the SendMessage-delivered WndProc — keeping
    // the OS broadcast unblocked. Also posted by the provider on monitor arrival.
    internal const uint WM_APP_REFRESH = 0x8000; // WM_APP + 0

    internal const uint PM_REMOVE = 0x0001;

    // Extended styles for the sink's hidden window. It is never shown, so it could
    // not appear in the shell regardless — but a bare top-level window relies on
    // "we never call ShowWindow" for that, whereas these bits make it the OS's
    // guarantee. WS_EX_TOOLWINDOW excludes the window from the taskbar and the
    // alt-tab switcher; WS_EX_NOACTIVATE stops it ever taking activation. Neither
    // affects delivery of the WM_DISPLAYCHANGE broadcast, which goes to top-level
    // windows regardless of these bits. Matters for kiosk hosts that suppress the
    // taskbar and assert on unexpected shell windows.
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClassW(string lpClassName, nint hInstance);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(MSG* lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessageW(MSG* lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? lpModuleName);
}
