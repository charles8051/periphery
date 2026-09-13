using System.Collections;
using System.Reflection;

namespace Periphery.FlashAnything.Tests;

/// <summary>
/// Reads decisions the service makes once and holds privately, for the tests whose only other
/// evidence was a timing race.
/// </summary>
/// <remarks>
/// A test that shows something did not run at the same time as something else needs a moment at
/// which it would have. The flash workers and the probe loop start on the thread pool and report
/// nothing until they act, so no such moment exists from outside. These read the decision instead:
/// how many workers were started, whether a family's app-mode flashes share a gate, whether a probe
/// loop was started (ADR-0089 D3). A renamed member fails here by name.
/// </remarks>
internal static class ServiceInternals
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>How many autoflash workers drain the queue.</summary>
    public static int AutoflashWorkerCount(FlashAnythingService svc) =>
        ((Array)Field(svc, "_autoflashWorkers")).Length;

    /// <summary>How many probe loops are running.</summary>
    public static int ProbeLoopCount(FlashAnythingService svc)
    {
        object loops = Field(svc, "_probeLoops");
        lock (Field(svc, "_gate"))
            return ((ICollection)loops).Count;
    }

    /// <summary>The gate an app-mode flash of <paramref name="family"/> waits on, or null when there is none.</summary>
    public static SemaphoreSlim? SerializationGateFor(FlashAnythingService svc, string family) =>
        (SemaphoreSlim?)typeof(FlashAnythingService)
            .GetMethod("SerializationGateFor", Private)!
            .Invoke(svc, [family]);

    private static object Field(FlashAnythingService svc, string name) =>
        typeof(FlashAnythingService).GetField(name, Private)?.GetValue(svc)
        ?? throw new MissingFieldException(nameof(FlashAnythingService), name);
}
