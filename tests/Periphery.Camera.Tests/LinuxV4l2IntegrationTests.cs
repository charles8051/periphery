using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Periphery;
using Periphery.Camera;
using Xunit;

namespace Periphery.Camera.Tests;

/// <summary>
/// Device-backed V4L2 tests. They run only on the Linux device rig
/// (the Linux device rig), where <c>PERIPHERY_LINUX_DEVICE_TESTS=1</c> and a
/// v4l2loopback device at /dev/video10 is fed a 640x480 YUYV test pattern by
/// the periphery-testpattern systemd service. On the rig, a missing device is
/// a hard failure — never a skip — so the rig cannot rot silently.
/// </summary>
[Collection(RigFixtures.Name)]
public class LinuxV4l2IntegrationTests
{
    private static bool Enabled =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("PERIPHERY_LINUX_DEVICE_TESTS") == "1";

    private static async Task<DeviceInfo> FindLoopbackCameraAsync()
    {
        // Every device test reaches hardware through here or FindVividCameraAsync, so this is
        // the one place the fake can be caught before it is mistaken for a device (#276).
        RigGuard.RequireRealBackend();

        var cameras = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Camera)
            .ToListAsync();

        var loopback = cameras.FirstOrDefault(c =>
                c.Name?.Contains("periphery-test", StringComparison.OrdinalIgnoreCase) == true)
            ?? cameras.FirstOrDefault(c => c.Id.Value.EndsWith("video10", StringComparison.Ordinal));

        // Hard failure, never a skip — see the class doc. The message carries the two causes
        // actually seen on this rig, so a red run is self-diagnosing rather than a puzzle:
        // the second one had /dev/video10 missing for an unknown stretch until 2026-08-21.
        Assert.True(loopback is not null,
            "v4l2loopback test camera not found. Two known causes on the Linux device rig: "
            + "(1) periphery-testpattern.service is not running — check "
            + "`systemctl status periphery-testpattern`; (2) a kernel bump left "
            + "linux-modules-extra-$(uname -r) uninstalled, so v4l2loopback cannot resolve "
            + "videodev symbols and /dev/video10 never appears — check `lsmod | grep v4l2loopback` "
            + "and `dmesg | grep v4l2loopback`. Skipping here is deliberately not an option: a "
            + "green run on a rig with no devices is how the rig rots unnoticed.");
        return loopback!;
    }

    /// <summary>
    /// The vivid (Virtual Video Test Driver) node, pinned to /dev/video20 by the rig's
    /// modprobe config. Distinct from the v4l2loopback fixture: loopback carries a test
    /// pattern but exposes no standard <c>V4L2_CID_*</c> controls at all, so control
    /// behaviour has to be exercised somewhere else (#255).
    /// </summary>
    private static async Task<DeviceInfo> FindVividCameraAsync()
    {
        RigGuard.RequireRealBackend();

        var cameras = await Devices.Enumerate()
            .OfCategory(DeviceCategory.Camera)
            .ToListAsync();

        var vivid = cameras.FirstOrDefault(c =>
                c.Name?.Contains("vivid", StringComparison.OrdinalIgnoreCase) == true)
            ?? cameras.FirstOrDefault(c => c.Id.Value.EndsWith("video20", StringComparison.Ordinal));

        // Hard failure, never a skip — same policy as the loopback fixture.
        Assert.True(vivid is not null,
            "vivid test camera not found. It is loaded by /etc/modules-load.d/periphery-test.conf "
            + "and pinned to /dev/video20 by /etc/modprobe.d/periphery-vivid.conf — check "
            + "`lsmod | grep vivid` and `v4l2-ctl -d /dev/video20 --info`. It comes from "
            + "linux-modules-extra-$(uname -r), so a kernel bump without that package removes it.");
        return vivid!;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Loopback_EnumeratesFormats_IncludingTestPattern()
    {
        if (!Enabled) return;

        var info = await FindLoopbackCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        var formats = await device.GetFormatsAsync(CancellationToken.None);

        Assert.NotEmpty(formats);
        Assert.Contains(formats, f =>
            f.PixelFormat == CameraPixelFormat.Yuy2 && f.Width == 640 && f.Height == 480);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Loopback_CapturesTestPatternFrames()
    {
        if (!Enabled) return;

        var info = await FindLoopbackCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        var formats = await device.GetFormatsAsync(CancellationToken.None);
        var format = formats.First(f =>
            f.PixelFormat == CameraPixelFormat.Yuy2 && f.Width == 640 && f.Height == 480);

        await using var session = await device.OpenSessionAsync(new CameraConfiguration(format));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int frames = 0;
        await foreach (var frame in session.CaptureAsync(ct: cts.Token))
        {
            using (frame)
            {
                Assert.Equal(640, frame.Width);
                Assert.Equal(480, frame.Height);
                Assert.Equal(CameraPixelFormat.Yuy2, frame.PixelFormat);

                // The ffmpeg test pattern is colour bars — real data, not zeroes.
                byte[] data = frame.ContiguousBuffer.ToArray();
                Assert.Equal(640 * 480 * 2, data.Length);
                Assert.Contains(data, b => b != 0);
            }

            if (++frames >= 5) break;
        }

        Assert.Equal(5, frames);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Loopback_StopAndRestartCapture_Works()
    {
        if (!Enabled) return;

        var info = await FindLoopbackCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        var formats = await device.GetFormatsAsync(CancellationToken.None);
        var format = formats.First(f =>
            f.PixelFormat == CameraPixelFormat.Yuy2 && f.Width == 640 && f.Height == 480);

        await using var session = await device.OpenSessionAsync(new CameraConfiguration(format));

        for (int round = 0; round < 2; round++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int frames = 0;
            await foreach (var frame in session.CaptureAsync(ct: cts.Token))
            {
                frame.Dispose();
                if (++frames >= 2) break;
            }
            Assert.Equal(2, frames);
            await session.StopCaptureAsync();
        }
    }

    // ── #256: the fd cannot be used across a teardown ─────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AClosedDescriptor_RefusesInterop_InsteadOfIssuingOnARecycledNumber()
    {
        // The bug this pins is NOT "a disposed backend refuses calls" — ThrowIfNotOpen already
        // did that, and a test written against it passes with or without the fix (confirmed by
        // negative control). The bug is the gap *between* that check and the ioctl: on Linux a
        // closed fd number is immediately reusable, so an ioctl issued on a number read before
        // teardown can land on a descriptor now belonging to something else, returning ENOTTY
        // and reading like a device that declined the control (#256).
        //
        // So this drives the descriptor layer directly, where the guarantee actually lives.
        if (!Enabled) return;

        // Discovered, not hard-coded: the fixture's node number is not this test's business,
        // and pinning /dev/video10 would fail the suite on a rig that assigns another one
        // (#273 review turn 1).
        var info = await FindLoopbackCameraAsync();
        // Through the backend's own resolver: DeviceInfo.Id is a sysfs path, not an openable
        // node, and duplicating that mapping in a test would be a second source of truth.
        string devNode = Linux.V4l2CameraBackend.ResolveDevNode(info.Id.Value);

        int raw = Linux.V4l2Interop.Open(
            devNode,
            Linux.V4l2Interop.O_RDWR | Linux.V4l2Interop.O_NONBLOCK | Linux.V4l2Interop.O_CLOEXEC);
        Assert.True(raw >= 0, $"could not open {devNode}");

        var handle = new Linux.V4l2FileDescriptor(raw);
        handle.Dispose();

        // Churn the fd table so `raw` is handed to something unrelated. Anything issued on the
        // bare number from here lands on one of these.
        var squatters = new List<FileStream>();
        try
        {
            for (int i = 0; i < 64; i++)
                squatters.Add(File.Open("/dev/null", FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

            // The ref-int ioctl overload, so this test needs no unsafe block. Which request it
            // is does not matter: the point is that the call is refused before any syscall.
            int bufferType = (int)Linux.V4l2Interop.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            Exception? through = Record.Exception(
                () => Linux.V4l2Interop.Ioctl(handle, Linux.V4l2Interop.VIDIOC_STREAMOFF, ref bufferType));

            // Marshalling ref-counts the handle, so a closed one is refused before any syscall.
            Assert.IsType<ObjectDisposedException>(through);
        }
        finally
        {
            foreach (var f in squatters) f.Dispose();
        }
    }

    // ── #255: control behaviour, against a driver that actually has controls ──

    /// <summary>
    /// Puts vivid's Gain back under automatic control.
    /// </summary>
    /// <remarks>
    /// vivid is a kernel module, so its control state outlives any <see cref="CameraDevice"/>
    /// that touched it — a test that leaves Gain in manual changes what the next test sees.
    /// These tests passed only because of the order they happened to run in, which xUnit does
    /// not promise (#273 review turn 10). Each one now establishes its own precondition and
    /// restores it afterwards, so they are order-independent and re-runnable.
    /// </remarks>
    private static async Task ResetGainToAutomaticAsync(CameraDevice device)
    {
        await device.ResetControlAsync(CameraControlKind.Gain);

        var state = await device.GetControlAsync(CameraControlKind.Gain);
        Assert.Equal(CameraControlMode.Automatic, state!.Mode);
    }

    /// <summary>
    /// vivid's generic menu control — not a camera control at all.
    /// </summary>
    /// <remarks>
    /// It exists so driver tests can exercise menu plumbing, and it is <b>sparse</b>: the range it
    /// advertises is 1..4 but entry 2 does not exist. That makes it the only fixture on this rig
    /// which can tell a bounds check apart from an actual query.
    /// </remarks>
    private const uint VividGenericMenu = 0x0098F904;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SupportsMenuEntry_RejectsAnAbsentEntryInsideTheAdvertisedRange()
    {
        // Recovers, on a VIRTUAL fixture, the regression cover that otherwise left CI when the
        // webcam suite did (#277). Note what this drives: V4l2CameraBackend.SupportsMenuEntry --
        // the PRODUCTION function EnforceCompanionMode calls -- not the raw wrapper beside it. An
        // earlier version of this test called RawV4l2.AdvertisesMenuEntry, which would have stayed
        // green if SupportsMenuEntry stopped querying altogether (#281 review turn 1).
        //
        // The bug #275 fixed lives in one line: passing the [Minimum, Maximum] bounds does not
        // mean an entry exists, because a menu may be SPARSE. vivid's generic menu is exactly
        // that -- range 1..4 with entry 2 absent -- so it separates a bounds check from a query
        // without any camera-class hardware.
        if (!Enabled) return;

        var info = await FindVividCameraAsync();
        string devNode = Linux.V4l2CameraBackend.ResolveDevNode(info.Id.Value);

        int raw = Linux.V4l2Interop.Open(
            devNode,
            Linux.V4l2Interop.O_RDWR | Linux.V4l2Interop.O_NONBLOCK | Linux.V4l2Interop.O_CLOEXEC);
        Assert.True(raw >= 0, $"could not open {devNode}");
        using var handle = new Linux.V4l2FileDescriptor(raw);

        Assert.True(RawV4l2.TryQueryControl(devNode, VividGenericMenu, out var query),
            $"vivid's generic menu 0x{VividGenericMenu:X8} could not be described on '{devNode}'. "
            + $"Check `v4l2-ctl -d {devNode} --list-ctrls-menus`.");
        Assert.Equal(Linux.V4l2Interop.V4L2_CTRL_TYPE_MENU, query.Type);

        var present = new List<int>();
        var absent = new List<int>();
        for (int entry = query.Minimum; entry <= query.Maximum; entry++)
        {
            (Linux.V4l2CameraBackend.SupportsMenuEntry(handle, VividGenericMenu, entry)
                ? present
                : absent).Add(entry);
        }

        // Without this, a SupportsMenuEntry that answered "no" to everything -- a broken request
        // code, a mis-sized v4l2_querymenu -- would satisfy the sparseness assertion below and
        // look like a correctly rejecting implementation.
        Assert.True(present.Count > 0,
            $"SupportsMenuEntry rejected every entry in {query.Minimum}..{query.Maximum} on "
            + $"0x{VividGenericMenu:X8}. That is what a wrong VIDIOC_QUERYMENU request code or a "
            + "mis-sized v4l2_querymenu looks like — not a sparse menu.");

        // The discriminating assertion. Every index here is INSIDE the advertised bounds, so an
        // implementation that bounds-checks and returns true would report them present.
        Assert.True(absent.Count > 0,
            $"SupportsMenuEntry accepted every entry in {query.Minimum}..{query.Maximum} on "
            + $"0x{VividGenericMenu:X8}, so it is not consulting VIDIOC_QUERYMENU — the #275 "
            + "defect exactly. (If vivid's control set changed and this menu is no longer sparse, "
            + "this rig can no longer cover that fix: find another sparse menu.)");

        // And the oracle agrees, independently of the production path.
        foreach (int entry in absent)
            Assert.False(RawV4l2.AdvertisesMenuEntry(devNode, VividGenericMenu, entry));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Vivid_GainUnderAutomatic_ReadsAsAutomatic()
    {
        // Named for what it asserts rather than "fresh": the driver's state persists across
        // opens, so "fresh" was never something a test could claim on its own.
        if (!Enabled) return;

        var info = await FindVividCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        await ResetGainToAutomaticAsync(device);

        var state = await device.GetControlAsync(CameraControlKind.Gain);

        Assert.NotNull(state);
        Assert.Equal(CameraControlMode.Automatic, state!.Mode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Vivid_SettingGain_TakesTheControlAwayFromTheDevice_AndTheValueSticks()
    {
        // The behaviour ADR-0077 D4 introduced and #255 flagged as unverified: V4L2 previously
        // wrote only the value, so on a device in auto mode the write was either refused with
        // EBUSY or overwritten on the next frame. The companion has to be switched to manual
        // first for the value to mean anything.
        if (!Enabled) return;

        var info = await FindVividCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        try
        {
            await ResetGainToAutomaticAsync(device);

            var range = (await device.GetControlsAsync())
                .First(c => c.Kind == CameraControlKind.Gain);
            double target = Math.Round(((range.MinValue ?? 0) + (range.MaxValue ?? 255)) / 2);

            await device.SetControlAsync(CameraControlKind.Gain, target);

            var after = await device.GetControlAsync(CameraControlKind.Gain);
            Assert.NotNull(after);
            Assert.Equal(CameraControlMode.Manual, after!.Mode);
            Assert.Equal(target, after.Value);

            // And still there a few frames later — the old behaviour drifted back as the
            // device's own loop reasserted itself.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var later = await device.GetControlAsync(CameraControlKind.Gain);
            Assert.Equal(CameraControlMode.Manual, later!.Mode);
            Assert.Equal(target, later.Value);
        }
        finally
        {
            await device.ResetControlAsync(CameraControlKind.Gain);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Vivid_ResettingGain_HandsItBackToTheDevice()
    {
        // ResetControlAsync restores automatic, mirroring Media Foundation's reset-with-_AUTO.
        // Without it a caller could pin a control and never give it back.
        if (!Enabled) return;

        var info = await FindVividCameraAsync();
        await using var device = await CameraDevice.OpenAsync(info);

        try
        {
            await device.SetControlAsync(CameraControlKind.Gain, 120);
            Assert.Equal(CameraControlMode.Manual,
                (await device.GetControlAsync(CameraControlKind.Gain))!.Mode);

            await device.ResetControlAsync(CameraControlKind.Gain);

            var state = await device.GetControlAsync(CameraControlKind.Gain);
            Assert.Equal(CameraControlMode.Automatic, state!.Mode);
        }
        finally
        {
            await device.ResetControlAsync(CameraControlKind.Gain);
        }
    }
}
