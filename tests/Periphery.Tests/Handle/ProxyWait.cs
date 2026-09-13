using System.ComponentModel;

namespace Periphery.Tests;

/// <summary>Waits on a proxy's own notifications instead of polling it (ADR-0089 D2).</summary>
internal static class ProxyWait
{
    /// <summary>Bounds a wait so a proxy that never gets there fails instead of hanging (ADR-0089 D5).</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Completes once <paramref name="condition"/> holds. It is checked on entry and again on every
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> the proxy raises, so it has to read state
    /// that is settled by the time one of those notifications follows.
    /// </summary>
    public static async Task UntilAsync(INotifyPropertyChanged proxy, Func<bool> condition)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, PropertyChangedEventArgs e)
        {
            if (condition())
                met.TrySetResult();
        }

        proxy.PropertyChanged += Check;
        try
        {
            if (condition())
                return;
            await met.Task.WaitAsync(Bound);
        }
        finally
        {
            proxy.PropertyChanged -= Check;
        }
    }
}
