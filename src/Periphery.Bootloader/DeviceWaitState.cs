// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using System;
using System.Collections.Immutable;

namespace Periphery.Bootloader;

/// <summary>
/// How the orchestrator decides which re-enumerated device corresponds to the application it just
/// rebooted (ADR-0063 DEC-005).
/// </summary>
public enum DeviceCorrelationMode
{
    /// <summary>
    /// No stable id survives the mode switch — the EFM8 HID bootloader is the shared
    /// <c>0x10C4:0xEAC9</c> for <em>every</em> EFM8 part. Correlate by debounce: ignore candidates
    /// already present when the wait arms, accept the first one to appear afterwards. Because the
    /// re-enumerated bootloader carries no distinguisher, two concurrent app-mode flashes would both
    /// accept the first-appearing bootloader — so no-serial families using this mode also serialize
    /// (one device at a time). Prefer <see cref="ByLocationPath"/> when the family exposes a stable USB
    /// port; <see cref="FirstAppearance"/> is the safe fallback for a family with neither serial nor port.
    /// </summary>
    FirstAppearance,

    /// <summary>
    /// A stable serial survives into the bootloader (e.g. an STM32 app whose DFU keeps the 96-bit
    /// UID). Correlate exactly by that serial — parallel-safe.
    /// </summary>
    BySerial,

    /// <summary>
    /// No serial survives, but the physical USB port does: a board does <em>not</em> change port when
    /// it resets, so the re-enumerated bootloader shares the application device's
    /// <see cref="DeviceInfo.LocationPath"/> (hardware-verified — the app and its EFM8 bootloader report
    /// an identical <c>LocationPath</c>). Correlate exactly by matching that expected location path.
    /// Like <see cref="BySerial"/> this is exact and parallel-safe — for no-serial families that DO
    /// expose a stable port (e.g. Treehopper/EFM8), it lets several boards reboot and correlate
    /// concurrently, each to the exact port it came from.
    /// </summary>
    ByLocationPath,
}

/// <summary>Lifecycle status of a <see cref="DeviceWaitState"/>.</summary>
public enum DeviceWaitStatus
{
    /// <summary>Accumulating the candidates already present, before the wait is armed.</summary>
    Collecting,

    /// <summary>Armed and waiting for a correlating candidate (or a timeout).</summary>
    Waiting,

    /// <summary>Terminal: a candidate correlated to this wait (see <see cref="DeviceWaitState.Correlated"/>).</summary>
    Correlated,

    /// <summary>Terminal: no candidate correlated before the shell's clock elapsed.</summary>
    TimedOut,
}

/// <summary>
/// The pure, immutable correlation core of <see cref="BootloaderEntryOrchestrator"/> — "which
/// re-enumerated device is the one I just rebooted, and has it shown up yet?" It is advanced by
/// candidate appeared/disappeared events and a single timeout signal, and never touches IO or the
/// clock: the shell owns the <see cref="DeviceWatcher"/> and the timeout clock (ADR-0052 functional
/// core / imperative shell; ADR-0063 DEC-003/DEC-005). Every transition returns a new state.
/// </summary>
/// <remarks>
/// <para>
/// The shell feeds in only candidates that already match the relevant
/// <see cref="DeviceFilter"/> (so the safety gate is enforced upstream); this core decides
/// <em>which</em> matching candidate corresponds to the rebooted device and <em>when</em>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Collect, then arm.</b> Candidates seen before <see cref="Arm"/> are the pre-existing set. For
/// <see cref="DeviceCorrelationMode.FirstAppearance"/> with <c>debouncePreExisting</c>, those are
/// ignored thereafter (the debounce window the ADR describes — a bootloader that was already
/// sitting on the bus is never mistaken for the one our reboot produced). A device that disappears
/// drops out of the baseline, so a genuine re-enumeration counts as fresh.
/// </description></item>
/// <item><description>
/// <b>Identity correlation</b> (<see cref="DeviceCorrelationMode.BySerial"/> /
/// <see cref="DeviceCorrelationMode.ByLocationPath"/>) matches the candidate whose
/// <see cref="DeviceInfo.SerialNumber"/> (resp. <see cref="DeviceInfo.LocationPath"/>) equals the
/// expected value, regardless of when it appeared — exact and parallel-safe. An already-present match
/// at <see cref="Arm"/> correlates immediately.
/// </description></item>
/// </list>
/// </remarks>
public sealed class DeviceWaitState
{
    // Id comparison: device instance ids are case-insensitive by contract (see DeviceFilter.WithId).
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    // Candidates currently observed present, keyed by DeviceInfo.Id. While Collecting this is the
    // pre-existing set; it also lets Arm re-check a BySerial / ByLocationPath match among already-present devices.
    private readonly ImmutableDictionary<string, DeviceInfo> _present;

    // FirstAppearance debounce baseline: ids frozen at Arm that are ignored thereafter. Empty until
    // Arm, and empty when the wait does not debounce (the app-liveness wait accepts pre-existing).
    private readonly ImmutableHashSet<string> _ignored;

    private readonly bool _debouncePreExisting;

    private DeviceWaitState(
        DeviceCorrelationMode mode,
        string? expectedSerial,
        string? expectedLocationPath,
        bool debouncePreExisting,
        DeviceWaitStatus status,
        DeviceInfo? correlated,
        ImmutableDictionary<string, DeviceInfo> present,
        ImmutableHashSet<string> ignored)
    {
        Mode = mode;
        ExpectedSerial = expectedSerial;
        ExpectedLocationPath = expectedLocationPath;
        _debouncePreExisting = debouncePreExisting;
        Status = status;
        Correlated = correlated;
        _present = present;
        _ignored = ignored;
    }

    /// <summary>The correlation strategy this wait applies.</summary>
    public DeviceCorrelationMode Mode { get; }

    /// <summary>The serial to correlate against in <see cref="DeviceCorrelationMode.BySerial"/>; otherwise <c>null</c>.</summary>
    public string? ExpectedSerial { get; }

    /// <summary>The USB port to correlate against in <see cref="DeviceCorrelationMode.ByLocationPath"/>; otherwise <c>null</c>.</summary>
    public string? ExpectedLocationPath { get; }

    /// <summary>The current lifecycle status.</summary>
    public DeviceWaitStatus Status { get; }

    /// <summary>The correlated device once <see cref="Status"/> is <see cref="DeviceWaitStatus.Correlated"/>; otherwise <c>null</c>.</summary>
    public DeviceInfo? Correlated { get; }

    /// <summary>True once the wait has reached a terminal status (<see cref="DeviceWaitStatus.Correlated"/> or <see cref="DeviceWaitStatus.TimedOut"/>).</summary>
    public bool IsComplete => Status is DeviceWaitStatus.Correlated or DeviceWaitStatus.TimedOut;

    /// <summary>
    /// Begins a wait in the <see cref="DeviceWaitStatus.Collecting"/> phase. Feed pre-existing
    /// candidates via <see cref="OnAppeared"/>, then <see cref="Arm"/>.
    /// </summary>
    /// <param name="mode">The correlation strategy.</param>
    /// <param name="debouncePreExisting">
    /// For <see cref="DeviceCorrelationMode.FirstAppearance"/>: when <c>true</c> (the bootloader
    /// re-enumeration wait) candidates present at <see cref="Arm"/> are ignored so only a freshly
    /// appearing device correlates; when <c>false</c> (the app-liveness wait) a pre-existing
    /// candidate is accepted. Ignored for the identity modes
    /// (<see cref="DeviceCorrelationMode.BySerial"/> / <see cref="DeviceCorrelationMode.ByLocationPath"/>).
    /// </param>
    /// <param name="expectedSerial">
    /// Required (non-null/non-empty) for <see cref="DeviceCorrelationMode.BySerial"/>; the serial that
    /// survives the mode switch into the bootloader.
    /// </param>
    /// <param name="expectedLocationPath">
    /// Required (non-null/non-empty) for <see cref="DeviceCorrelationMode.ByLocationPath"/>; the USB
    /// port the device stays on across the mode switch.
    /// </param>
    public static DeviceWaitState Collecting(
        DeviceCorrelationMode mode, bool debouncePreExisting,
        string? expectedSerial = null, string? expectedLocationPath = null)
    {
        if (mode == DeviceCorrelationMode.BySerial && string.IsNullOrEmpty(expectedSerial))
            throw new ArgumentException(
                "BySerial correlation requires a non-empty expected serial.", nameof(expectedSerial));
        // IsNullOrWhiteSpace: a whitespace-only port is as useless as an empty one — it can never match
        // a real platform-supplied LocationPath, so reject it at construction, symmetric with the intent.
        if (mode == DeviceCorrelationMode.ByLocationPath && string.IsNullOrWhiteSpace(expectedLocationPath))
            throw new ArgumentException(
                "ByLocationPath correlation requires a non-empty expected location path.", nameof(expectedLocationPath));

        return new DeviceWaitState(
            mode, expectedSerial, expectedLocationPath, debouncePreExisting, DeviceWaitStatus.Collecting,
            correlated: null,
            ImmutableDictionary.Create<string, DeviceInfo>(IdComparer),
            ImmutableHashSet.Create<string>(IdComparer));
    }

    /// <summary>
    /// Records a matching candidate as present. While <see cref="DeviceWaitStatus.Collecting"/> this
    /// only accumulates the pre-existing set (correlation is deferred to <see cref="Arm"/>). While
    /// <see cref="DeviceWaitStatus.Waiting"/> a correlating candidate transitions to
    /// <see cref="DeviceWaitStatus.Correlated"/>. A no-op once terminal.
    /// </summary>
    public DeviceWaitState OnAppeared(DeviceInfo candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IsComplete) return this;

        var present = _present.SetItem(candidate.Id, candidate);
        if (Status == DeviceWaitStatus.Waiting && Correlates(candidate))
            return Correlate(candidate);

        return With(status: Status, correlated: null, present: present, ignored: _ignored);
    }

    /// <summary>
    /// Records that a candidate left the bus. It drops out of the present set and the debounce
    /// baseline, so a later re-appearance counts as fresh (a real re-enumeration). A no-op once terminal.
    /// </summary>
    public DeviceWaitState OnDisappeared(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        if (IsComplete) return this;

        var present = _present.Remove(deviceId);
        var ignored = _ignored.Remove(deviceId);
        if (ReferenceEquals(present, _present) && ReferenceEquals(ignored, _ignored))
            return this;
        return With(status: Status, correlated: null, present: present, ignored: ignored);
    }

    /// <summary>
    /// Arms the wait: freezes the debounce baseline and re-checks the pre-existing candidates. For the
    /// identity modes (<see cref="DeviceCorrelationMode.BySerial"/> /
    /// <see cref="DeviceCorrelationMode.ByLocationPath"/>) — or a non-debouncing
    /// <see cref="DeviceCorrelationMode.FirstAppearance"/> — an already-present correlating candidate
    /// transitions straight to <see cref="DeviceWaitStatus.Correlated"/>. A no-op unless still
    /// <see cref="DeviceWaitStatus.Collecting"/>.
    /// </summary>
    public DeviceWaitState Arm()
    {
        if (Status != DeviceWaitStatus.Collecting) return this;

        var ignored = (_debouncePreExisting && Mode == DeviceCorrelationMode.FirstAppearance)
            ? _present.Keys.ToImmutableHashSet(IdComparer)
            : ImmutableHashSet.Create<string>(IdComparer);

        var armed = new DeviceWaitState(
            Mode, ExpectedSerial, ExpectedLocationPath, _debouncePreExisting, DeviceWaitStatus.Waiting,
            correlated: null, _present, ignored);

        foreach (var candidate in armed._present.Values)
        {
            if (armed.Correlates(candidate))
                return armed.Correlate(candidate);
        }
        return armed;
    }

    /// <summary>
    /// Advances the wait by the shell's timeout clock. Transitions to
    /// <see cref="DeviceWaitStatus.TimedOut"/> unless already terminal (a correlation that landed
    /// first is never overridden).
    /// </summary>
    public DeviceWaitState OnTimeout()
    {
        if (IsComplete) return this;
        return new DeviceWaitState(
            Mode, ExpectedSerial, ExpectedLocationPath, _debouncePreExisting, DeviceWaitStatus.TimedOut,
            correlated: null, _present, _ignored);
    }

    private bool Correlates(DeviceInfo candidate) => Mode switch
    {
        DeviceCorrelationMode.BySerial =>
            ExpectedSerial is not null &&
            string.Equals(candidate.SerialNumber, ExpectedSerial, StringComparison.OrdinalIgnoreCase),
        // ByLocationPath: the physical USB port survives the mode switch, so the bootloader that
        // re-enumerated on the app device's port is exactly ours — regardless of appearance order.
        // The `is not null` is purely defensive: Collecting() guarantees a non-empty ExpectedLocationPath
        // for this mode, but it also stops a candidate with a null LocationPath matching a null expected.
        DeviceCorrelationMode.ByLocationPath =>
            ExpectedLocationPath is not null &&
            string.Equals(candidate.LocationPath, ExpectedLocationPath, StringComparison.OrdinalIgnoreCase),
        // FirstAppearance: any matching candidate correlates unless it is in the debounce baseline.
        _ => !_ignored.Contains(candidate.Id),
    };

    private DeviceWaitState Correlate(DeviceInfo candidate) => new(
        Mode, ExpectedSerial, ExpectedLocationPath, _debouncePreExisting, DeviceWaitStatus.Correlated,
        candidate, _present, _ignored);

    private DeviceWaitState With(
        DeviceWaitStatus status, DeviceInfo? correlated,
        ImmutableDictionary<string, DeviceInfo> present, ImmutableHashSet<string> ignored)
        => new(Mode, ExpectedSerial, ExpectedLocationPath, _debouncePreExisting, status, correlated, present, ignored);
}
