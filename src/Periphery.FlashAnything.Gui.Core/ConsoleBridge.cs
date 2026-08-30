// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Periphery.FlashAnything.Gui;

/// <summary>
/// Connects a windowed (<c>WinExe</c>) process to the terminal that launched it, so the single
/// dual-mode binary can also print CLI output. On Windows a <c>WinExe</c> starts with no console of
/// its own; <see cref="AttachToParentConsole"/> attaches to the launching shell's console (when there
/// is one) and rebinds <see cref="Console"/> to it.
/// </summary>
internal static class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF; // ATTACH_PARENT_PROCESS

    // A blittable signature, so DllImport is AOT-clean here and avoids enabling unsafe code project-wide
    // (LibraryImport's source-generated marshalling would require <AllowUnsafeBlocks>).
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>
    /// Attaches to the launching terminal's console if there is one, rebinding
    /// <see cref="Console.Out"/> / <see cref="Console.Error"/> to it so a <c>WinExe</c>'s CLI output
    /// reaches the shell. A no-op on non-Windows, where a process launched from a shell already has a
    /// usable stdout, and harmless when there is no parent console (output simply has nowhere to go).
    /// </summary>
    public static void AttachToParentConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // When stdout is already redirected (a pipe / file), the standard handle is set and
        // AttachConsole leaves it alone — output still flows where it was redirected. When it is not
        // (a bare WinExe), AttachConsole binds the std handles to the parent console's buffers.
        if (!AttachConsole(AttachParentProcess))
            return;

        // .NET latched no-op console writers at startup (it found no console); rebind them to the
        // now-attached handles, auto-flushing so a short-lived CLI run isn't lost on exit.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
