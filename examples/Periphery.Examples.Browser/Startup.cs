namespace Periphery.Examples.Browser;

/// <summary>What started, and what went wrong while the page started, shown at the top of the page.</summary>
internal static class Startup
{
    /// <summary>Whether <c>BrowserUsb.InitializeAsync</c> completed.</summary>
    public static bool UsbReady { get; set; }

    /// <summary>Whether <c>BrowserSerial.InitializeAsync</c> completed.</summary>
    public static bool SerialReady { get; set; }

    public static List<string> Problems { get; } = new();
}
