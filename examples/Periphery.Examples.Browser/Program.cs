using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Periphery.Examples.Browser;
using Periphery.Serial.Browser;
using Periphery.Usb.Browser;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Before any click: the browser shows its chooser only briefly after a gesture, and loading the
// JS modules inside that window would spend it (ADR-0092 D4). A browser without WebUSB or Web
// Serial throws here; the page shows why instead of failing to start.
try
{
    await BrowserUsb.InitializeAsync();
}
catch (PlatformNotSupportedException e)
{
    Startup.Problems.Add(e.Message);
}

try
{
    await BrowserSerial.InitializeAsync();
}
catch (PlatformNotSupportedException e)
{
    Startup.Problems.Add(e.Message);
}

await builder.Build().RunAsync();
