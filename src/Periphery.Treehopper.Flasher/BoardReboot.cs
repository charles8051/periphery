// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

namespace Periphery.Treehopper.Flasher;

/// <summary>Which way the board crossed the USB bus, as the OS reported it.</summary>
public enum RebootSignalKind
{
    /// <summary>The board left the bus — a removal, or the deactivation that cascades from one.</summary>
    Gone,

    /// <summary>The board is on the bus — an arrival, or the activation that follows one.</summary>
    Back,
}

/// <summary>
/// One OS device notification, stamped with how long after the reboot write it arrived.
/// </summary>
/// <param name="Kind">Which way the board crossed the bus.</param>
/// <param name="At">Elapsed time since <c>0x0C</c> was written, as the shell's clock measured it.</param>
public readonly record struct RebootSignal(RebootSignalKind Kind, TimeSpan At);

/// <summary>What a completed watch proves about the reboot.</summary>
public enum RebootOutcome
{
    /// <summary>The board left the bus and came back: the reset reached the firmware.</summary>
    Rebooted,

    /// <summary>The board left the bus and had not returned when the watch ran out.</summary>
    DroppedWithoutReturn,

    /// <summary>The board never left the bus. With an event-driven watch this is a real negative.</summary>
    NoDropObserved,
}

/// <summary>
/// The accumulated state of one reboot watch: when the board left the bus, and when it came back.
/// A value, folded forward one <see cref="RebootSignal"/> at a time by <see cref="Observe"/>.
/// </summary>
public readonly record struct RebootObservation
{
    /// <summary>When the board left the bus, relative to the reboot write; <c>null</c> if it never did.</summary>
    public TimeSpan? DroppedAt { get; init; }

    /// <summary>When the board came back, relative to the reboot write; <c>null</c> if it has not.</summary>
    public TimeSpan? ReturnedAt { get; init; }

    /// <summary>True once both edges are in: the watch has nothing left to wait for.</summary>
    public bool IsComplete => DroppedAt is not null && ReturnedAt is not null;

    /// <summary>How long the board was absent, once both edges are in; otherwise <c>null</c>.</summary>
    public TimeSpan? Gap => DroppedAt is { } dropped && ReturnedAt is { } returned ? returned - dropped : null;

    /// <summary>
    /// Folds one notification into the observation. Pure and total: every signal yields a value, and
    /// a signal that carries no new information yields this same value back.
    /// </summary>
    /// <remarks>
    /// Only the <em>first</em> edge in each direction counts, and a <see cref="RebootSignalKind.Back"/>
    /// before any drop is discarded. That is what makes the fold safe against the two duplicate-event
    /// shapes the watcher legitimately produces: the initial snapshot activates the board that is
    /// already present (a <c>Back</c> before the drop), and one physical transition arrives as both a
    /// deactivation and a removal, or as both an arrival and an activation (a second edge the same way).
    /// </remarks>
    public RebootObservation Observe(RebootSignal signal) => signal.Kind switch
    {
        RebootSignalKind.Gone when DroppedAt is null && ReturnedAt is null => this with { DroppedAt = signal.At },
        RebootSignalKind.Back when DroppedAt is not null && ReturnedAt is null => this with { ReturnedAt = signal.At },
        _ => this,
    };
}

/// <summary>
/// The pure core of the Treehopper Flasher's <c>reboot</c> verb (ADR-0052): folding OS device
/// notifications into a verdict, and rendering that verdict, are total functions over values — no USB,
/// no clock, no console. <c>RebootVerb</c> is the thin shell that writes <c>0x0C</c> and runs the clock.
/// </summary>
public static class BoardReboot
{
    /// <summary>Classifies a watch that has ended, either by completing or by running out of time.</summary>
    public static RebootOutcome Classify(RebootObservation observation) => observation switch
    {
        { IsComplete: true } => RebootOutcome.Rebooted,
        { DroppedAt: not null } => RebootOutcome.DroppedWithoutReturn,
        _ => RebootOutcome.NoDropObserved,
    };

    /// <summary>
    /// The one-line verdict for a watch that has ended. Reports the measured absence when there is
    /// one, so a short transient reads as the real, short thing it is rather than as flaky hardware.
    /// </summary>
    /// <param name="observation">The folded watch.</param>
    /// <param name="budget">How long the watch was allowed to run — quoted in the two negative verdicts.</param>
    /// <remarks>
    /// <b>Both edges are stamped when the OS notification arrived</b>, not when the bus transition
    /// happened, which is why the line reports both edges and not only the gap. On an idle box they
    /// agree with the bus: the board leaves ~15 ms after the write and is back ~245 ms after it, an
    /// absence of ~230 ms. Under load the two notifications can arrive late and bunched — measured
    /// once as <c>257 ms</c> / <c>267 ms</c> for the same reboot — which compresses the gap without
    /// moving the verdict. Printing both edges is what makes that visible instead of silent.
    /// </remarks>
    // Matching on the observation itself rather than on Classify's verdict: the patterns bind the
    // times they need, so their presence is a property of the match instead of a claim the reader
    // has to go and check against Classify. The arms are Classify's cases in the same order.
    public static string Summarize(RebootObservation observation, TimeSpan budget) => observation switch
    {
        { DroppedAt: { } dropped, ReturnedAt: { } returned } =>
            $"OK - left the USB bus {Ms(dropped)} after the write, "
          + $"back at {Ms(returned)} (absent ~{Ms(returned - dropped)})",
        { DroppedAt: { } dropped } =>
            $"WARNING - dropped off USB after {Ms(dropped)} but had not returned after {Seconds(budget)}",
        // The negative is now worth trusting, and says so: the watch is the OS's own device
        // notifications, not a poll, so an absence of any length would have raised an event.
        _ => $"NO EFFECT - the board never left the USB bus in {Seconds(budget)} "
           + "(watched live via OS device notifications, so a drop of any length would have been seen)",
    };

    /// <summary>
    /// The one-line verdict for an <b>out-of-band rescue</b> watch that has ended (ADR-0075).
    /// </summary>
    /// <param name="observation">The folded watch.</param>
    /// <param name="budget">How long the watch was allowed to run — quoted in the two negative verdicts.</param>
    /// <remarks>
    /// <para>
    /// Same fold, different stakes, so different words. For a <c>reboot</c> the write itself
    /// succeeding is already evidence the endpoint was alive, and the watch adds confirmation. For
    /// a rescue <b>the watch is the only evidence there is</b>: the control transfer faults whether
    /// the board reset or the firmware never implemented the request, so nothing but re-enumeration
    /// distinguishes them.
    /// </para>
    /// <para>
    /// That makes the negative verdict say something different too. A board that never left the bus
    /// did not ignore a command it received — the most likely reading is firmware with no rescue
    /// handler, which is every board flashed before periphery#227. The wording says so rather than
    /// implying broken hardware.
    /// </para>
    /// </remarks>
    public static string SummarizeRescue(RebootObservation observation, TimeSpan budget) => observation switch
    {
        { DroppedAt: { } dropped, ReturnedAt: { } returned } =>
            $"RESCUED - left the USB bus {Ms(dropped)} after the request, "
          + $"back at {Ms(returned)} (absent ~{Ms(returned - dropped)})",
        { DroppedAt: { } dropped } =>
            $"WARNING - dropped off USB after {Ms(dropped)} but had not returned after {Seconds(budget)}",
        _ => $"NO RESCUE - the board never left the USB bus in {Seconds(budget)}. The request was sent "
           + "and its failure proves nothing, so this is the only signal there is: most likely this "
           + "firmware has no rescue handler (anything flashed before it was added ignores the request).",
    };

    /// <summary>
    /// True if <paramref name="candidate"/> is the board named by <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer the serial, which survives a reboot — it lives in a separate config area, not the app
    /// firmware. Fall back to the instance id, which is stable for a reboot in place (it encodes the
    /// hub-port path for a board with no serial) but would not survive a move to another port.
    /// </para>
    /// <para>
    /// The serial is matched against the candidate's own serial <em>or</em> the <em>last segment</em>
    /// of its instance id, because a removal notification can carry an id-only stub: the device is
    /// already out of the tree, so nothing is left to read a serial from. It is a segment match and
    /// not a substring match on purpose — a substring would accept a board whose serial merely
    /// <em>contains</em> this one (<c>ABCDEFG</c> found inside <c>…\XABCDEFGY</c>), and folding
    /// another board's edges into this watch is exactly the failure this predicate exists to prevent.
    /// </para>
    /// <para>
    /// <b>Every comparison here is case-insensitive, deliberately.</b> A device instance id is
    /// case-insensitive by contract and the same board really does re-enumerate with different casing
    /// (<c>…\CDYHINBH</c> became <c>…\cDYhINBh</c> across a measured reboot, periphery#231), which
    /// carries the serial along with it — the Windows provider derives the serial from the last
    /// segment of the instance id, and the firmware's serial alphabet really does include both cases.
    /// <see cref="DeviceId"/> already holds that invariant in its own equality; the string comparisons
    /// here spell it out. The predicate this replaced compared serials with <c>==</c>, i.e. ordinally,
    /// which made a board that came back look like a board that never did.
    /// </para>
    /// </remarks>
    public static bool IsSameBoard(DeviceInfo candidate, DeviceInfo target)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(target);

        if (target.SerialNumber is not { Length: > 0 } serial)
            return candidate.Id == target.Id;

        return string.Equals(candidate.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)
            || IdEndsWithSegment(candidate.Id.Value, serial);
    }

    /// <summary>
    /// True if <paramref name="serial"/> is the whole last segment of the instance id
    /// <paramref name="id"/> — not merely a substring of it. Both Windows (<c>\</c>) and the
    /// sysfs-style ids periphery surfaces elsewhere (<c>/</c>) count as segment separators.
    /// </summary>
    private static bool IdEndsWithSegment(string id, string serial)
    {
        if (!id.EndsWith(serial, StringComparison.OrdinalIgnoreCase)) return false;

        int start = id.Length - serial.Length;
        return start == 0 || id[start - 1] is '\\' or '/';
    }

    private static string Ms(TimeSpan span) => $"{span.TotalMilliseconds:0} ms";

    private static string Seconds(TimeSpan span) => $"{span.TotalSeconds:0.#}s";
}
