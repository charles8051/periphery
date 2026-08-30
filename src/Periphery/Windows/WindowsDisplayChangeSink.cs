// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using static Periphery.Windows.WindowMessageInterop;

namespace Periphery.Windows;

/// <summary>
/// Thin imperative shell that owns a hidden top-level window and a dedicated
/// message-pump thread, invoking a caller-supplied callback when a display
/// topology / mode / resolution / rotation change occurs. It is the OS push
/// signal the <see cref="WindowsDeviceMonitorProvider"/> otherwise lacks
/// (issue #149).
///
/// <para><b>Threading model.</b> <c>WM_DISPLAYCHANGE</c> is a system broadcast
/// delivered by <c>SendMessage</c> to top-level windows, so the sink uses a real
/// (never-shown) top-level window and handles it in the <c>WndProc</c>. To avoid
/// doing slow work — a full <c>QueryDisplayConfig</c> plus synchronous consumer
/// fan-out — inside that <c>SendMessage</c> path (which would block the OS
/// broadcast to every other window), the <c>WndProc</c> only <c>PostMessage</c>s
/// a private <c>WM_APP_REFRESH</c> to its own queue and returns immediately. The
/// pump loop then coalesces bursts and runs the callback off the broadcast path.
/// <see cref="RequestRefresh"/> feeds the same queue, so a fresh monitor arrival
/// (whose devnode the OS may enable before or after the topology settles) also
/// triggers a re-enrich — closing the arrival/topology ordering race.</para>
///
/// <para><b>The window is never user-visible.</b> It is zero-size, never
/// <c>ShowWindow</c>n (no <c>WS_VISIBLE</c>), and created
/// <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c>, so the shell excludes it from the
/// taskbar and the alt-tab switcher and it can never take activation. It is
/// still a top-level window and therefore still visible to <c>EnumWindows</c>
/// (class <c>PeripheryDisplayChangeSink_*</c>) — unavoidable, since only
/// top-level windows receive the <c>WM_DISPLAYCHANGE</c> broadcast. Relevant to
/// kiosk hosts that suppress the taskbar and audit top-level windows.</para>
///
/// <para><b>Never blocks the host.</b> Window-creation failure degrades
/// gracefully — the sink logs and stops, and the provider simply has no display
/// refresh hook (its pre-#149 behaviour) rather than failing the watcher — and
/// <see cref="Start"/>'s readiness wait and <see cref="Dispose"/>'s join are both
/// bounded, so neither a startup stall nor a wedged consumer handler can hang a
/// host's boot or shutdown.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDisplayChangeSink : IDisposable
{
    private static readonly ILogger<WindowsDisplayChangeSink> _logger =
        PeripheryLoggerFactory.CreateLogger<WindowsDisplayChangeSink>();

    private static int _classSeq;

    /// <summary>
    /// Backstop for <see cref="Start"/>'s readiness wait. Normally signalled in
    /// microseconds — creating a hidden window is cheap — so this only ever fires
    /// on a pathological stall, and never blocks a host's startup indefinitely.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    private readonly Action _onDisplayChange;
    private readonly string _className =
        $"PeripheryDisplayChangeSink_{Environment.ProcessId}_{Interlocked.Increment(ref _classSeq)}";
    private readonly ManualResetEventSlim _ready = new(false);

    // Cross-thread state. _pumpThreadId and _window are written on the pump
    // thread and read from arbitrary threads (RequestRefresh, Dispose);
    // _pumpThread is written by Start() on the caller's thread and read by
    // Dispose() on (potentially) another. EVERY access goes through
    // Volatile.Read/Volatile.Write.
    //
    // Why not just lean on _ready: the readiness wait is the only thing that
    // would otherwise order these, and it is deliberately BOUNDED (see Start).
    // On the timeout path the caller proceeds having taken no fence at all, so a
    // plain read may be hoisted out of a loop or served from a stale cache and
    // never observe the pump thread's publication — the wait is a liveness aid,
    // not a memory barrier. (`volatile` on the fields would work too — nint/uint
    // are both permitted field types — but the explicit calls mark exactly which
    // accesses cross a thread boundary, which is the part worth being able to
    // see in a file this racy.)
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private nint _window;
    private int _disposed;

    internal WindowsDisplayChangeSink(Action onDisplayChange)
        => _onDisplayChange = onDisplayChange;

    /// <summary>
    /// Starts the pump thread and waits (bounded) until the window is live or its
    /// creation has failed, so <see cref="RequestRefresh"/> and
    /// <see cref="Dispose"/> can rely on the window/queue existing.
    /// <para>The wait is bounded deliberately: this runs on the caller's startup
    /// path (<c>DeviceWatcher.StartAsync</c>), and a host that embeds Periphery —
    /// a kiosk, say — must never be unable to boot because a display-notification
    /// window misbehaved. On timeout the sink is simply treated as unavailable and
    /// the provider degrades to having no display refresh hook, which is the same
    /// graceful degradation as a failed window creation.</para>
    /// <para>Because it is bounded, the wait cannot be the thing that makes the
    /// pump thread's writes visible to later callers — hence the
    /// <c>Volatile</c> accesses on the cross-thread fields.</para>
    /// </summary>
    internal void Start()
    {
        var pump = new Thread(PumpThreadBody)
        {
            IsBackground = true,
            Name = "Periphery.DisplayChangeSink",
        };

        // Published before the thread starts, so Dispose() on any thread can see
        // it as soon as there is anything to join.
        Volatile.Write(ref _pumpThread, pump);
        pump.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            _logger.LogWarning(
                "WM_DISPLAYCHANGE sink did not signal readiness within {Seconds}s; continuing without a display refresh hook. Monitor DisplayConfig fields will not be re-stamped on hotplug.",
                StartupTimeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Requests a display-config refresh out of band (e.g. on a monitor devnode
    /// arrival). Coalesces with any pending <c>WM_DISPLAYCHANGE</c>-driven
    /// refresh. Safe to call from any thread; a no-op before the window exists or
    /// after teardown.
    /// </summary>
    internal void RequestRefresh()
    {
        // Volatile: this can run on a thread that never synchronised with the
        // pump (including a caller that came through Start()'s timeout path), and
        // the handle is both published and zeroed over there. Racing teardown is
        // still possible by construction — the window can be destroyed between
        // the read and the post — but PostMessage to a dead handle just fails,
        // which is exactly the no-op this method promises after teardown.
        nint window = Volatile.Read(ref _window);
        if (window != 0)
            PostMessageW(window, WM_APP_REFRESH, 0, 0);
    }

    private void PumpThreadBody()
    {
        // Declared out here so the finally can still unregister the class, but
        // ASSIGNED inside the try: anything that throws before the try would skip
        // the finally's _ready.Set() and leave Start() waiting forever.
        nint hInstance = 0;
        bool classRegistered = false;

        try
        {
            Volatile.Write(ref _pumpThreadId, GetCurrentThreadId());
            hInstance = GetModuleHandleW(null);

            if (!TryCreateWindow(hInstance, out classRegistered))
                return;

            _logger.LogDebug(
                "WM_DISPLAYCHANGE sink started (window 0x{Hwnd:X}).", Volatile.Read(ref _window));
            _ready.Set(); // window live — safe to receive WM_APP_REFRESH / WM_QUIT

            RunMessageLoop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WM_DISPLAYCHANGE sink pump crashed.");
        }
        finally
        {
            _ready.Set(); // idempotent; unblocks Start()/Dispose if we bailed early
            nint window = Volatile.Read(ref _window);
            if (window != 0)
            {
                DestroyWindow(window);
                Volatile.Write(ref _window, 0); // stops RequestRefresh posting to a dead handle
            }
            if (classRegistered)
                UnregisterClassW(_className, hInstance);
            _logger.LogDebug("WM_DISPLAYCHANGE sink stopped.");
        }
    }

    private unsafe bool TryCreateWindow(nint hInstance, out bool classRegistered)
    {
        classRegistered = false;

        nint classNamePtr = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize        = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc   = &WndProc,
                hInstance     = hInstance,
                lpszClassName = classNamePtr,
            };
            if (RegisterClassExW(&wc) == 0)
            {
                _logger.LogWarning(
                    "WM_DISPLAYCHANGE sink: RegisterClassEx failed (error {Err}); monitor display-change refresh disabled.",
                    Marshal.GetLastPInvokeError());
                return false;
            }
            classRegistered = true;
        }
        finally
        {
            // RegisterClassEx copies the name, so the buffer can go now.
            Marshal.FreeHGlobal(classNamePtr);
        }

        // hWndParent = 0 → a real top-level window (NOT HWND_MESSAGE) so it
        // receives the WM_DISPLAYCHANGE broadcast. Never shown (no WS_VISIBLE, no
        // ShowWindow), zero-size, and marked tool-window + no-activate so the shell
        // excludes it from the taskbar and alt-tab by OS rule rather than by our
        // convention of not showing it.
        nint window = CreateWindowExW(
            dwExStyle: WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            lpClassName: _className, lpWindowName: null, dwStyle: 0,
            x: 0, y: 0, nWidth: 0, nHeight: 0,
            hWndParent: 0, hMenu: 0, hInstance: hInstance, lpParam: 0);

        // Publish once, whatever the outcome: on failure this is 0, which is what
        // both RequestRefresh and the pump's finally already treat as "no window".
        Volatile.Write(ref _window, window);

        if (window == 0)
        {
            _logger.LogWarning(
                "WM_DISPLAYCHANGE sink: CreateWindowEx failed (error {Err}); monitor display-change refresh disabled.",
                Marshal.GetLastPInvokeError());
            return false;
        }

        return true;
    }

    private unsafe void RunMessageLoop()
    {
        MSG msg;
        int rc;
        // WM_QUIT (posted by Dispose) makes GetMessage return 0, ending the loop.
        while ((rc = GetMessageW(&msg, 0, 0, 0)) != 0)
        {
            if (rc == -1)
            {
                _logger.LogWarning(
                    "WM_DISPLAYCHANGE sink: GetMessage error {Err}; stopping pump.",
                    Marshal.GetLastPInvokeError());
                break;
            }

            if (msg.message == WM_APP_REFRESH)
            {
                // Coalesce a burst of refresh requests into one enrich pass.
                MSG drain;
                while (PeekMessageW(&drain, msg.hwnd, WM_APP_REFRESH, WM_APP_REFRESH, PM_REMOVE)) { }

                try
                {
                    _onDisplayChange();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Display-change handler threw.");
                }
                continue; // consumed; no dispatch
            }

            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    // Static AOT-safe window procedure. It does no per-instance work: on a
    // WM_DISPLAYCHANGE broadcast it hands off to the owning thread's pump via a
    // posted WM_APP_REFRESH (so this SendMessage-delivered call returns fast and
    // never blocks the OS broadcast), and defers everything else to the default.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DISPLAYCHANGE)
            PostMessageW(hWnd, WM_APP_REFRESH, 0, 0);

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Reentrancy guard: a display-change handler running on the pump thread
        // could dispose the watcher (and hence this sink). Joining our own thread
        // would deadlock, so just ask the loop to quit and return.
        uint pumpThreadId = Volatile.Read(ref _pumpThreadId);
        if (GetCurrentThreadId() == pumpThreadId)
        {
            PostThreadMessageW(pumpThreadId, WM_QUIT, 0, 0);
            return;
        }

        // Ensure the pump owns a message queue before posting to its thread.
        _ready.Wait(TimeSpan.FromSeconds(2));

        // Re-read: the id may only have been published while we waited — and if
        // the wait timed out, this read is the only thing ordering it at all.
        pumpThreadId = Volatile.Read(ref _pumpThreadId);
        if (pumpThreadId != 0)
            PostThreadMessageW(pumpThreadId, WM_QUIT, 0, 0);

        if (Volatile.Read(ref _pumpThread) is { } t && !t.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning(
                "WM_DISPLAYCHANGE sink pump did not exit within 5s; a display-change handler may be blocked.");
        }

        // Intentionally do NOT dispose _ready: if the join above timed out, the
        // pump thread's finally may still call _ready.Set(), and Set() on a
        // disposed ManualResetEventSlim throws — on a background thread that is
        // process-fatal. It is a lightweight primitive; let GC reclaim it.
    }
}
