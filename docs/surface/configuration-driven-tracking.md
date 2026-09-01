# Configuration-Driven Device Tracking

This pattern shows how to define tracked devices in `appsettings.json`, deserialize them into `DeviceTracker` instances at startup, wire events once, and run them inside an `IHostedService`. Trackers survive watcher restarts — event handlers and `IObservable<DeviceTrackerState>` subscriptions remain attached across the full service lifecycle.

---

## 1. Configuration

```json
// appsettings.json — keyed, so an overlay can override one entry by name
{
  "DeviceTracking": {
    "Devices": {
      "PrimaryMouse":    { "Category": "Usb",       "VendorId": "046D", "ProductId": "C52B" },
      "Dock":            { "Category": "Usb",       "SerialNumber": "DOCK-001" },
      "Headphones":      { "Category": "Bluetooth", "DeviceName": "AirPods" },
      "ExternalDisplay": { "Category": "Monitor",   "Manufacturer": "Dell" }
    }
  }
}
```

Each value binds straight to a `DeviceFilterSpec`; the key becomes the tracker
name. A list would work too, but a dictionary is what lets a per-machine overlay
override `PrimaryMouse` without restating the other three.

---

## 2. Options DTOs

**Do not hand-write the binder.** Periphery ships `DeviceFilterSpec` — one
property per `DeviceFilter` criterion, bindable from `IConfiguration` or JSON
with no adapter, and replayed by `DeviceFilter.Apply`. A test in the library
asserts every criterion has a spec property, so the spec cannot fall behind the
filter the way a hand-written DTO does.

```csharp
public sealed class DeviceTrackingOptions
{
    // Keyed, not a list, so a per-machine overlay can override one entry by
    // name without restating the others.
    public Dictionary<string, DeviceFilterSpec> Devices { get; set; } = new();
}
```

That is the whole DTO layer. The dictionary key becomes the tracker name:

```csharp
_trackers = options.Value.Devices
    .Select(kv => new DeviceTracker(DeviceProfile.FromSpec(kv.Value, kv.Key)))
    .ToArray();
```

`DeviceProfile.FromSpec` throws if the spec sets no criteria, with the spec's
description and the profile name in the message — so a typo in an overlay
surfaces against the configuration key an operator actually wrote, rather than
against a `configure` parameter they never saw.

To validate before constructing anything, ask the spec:

```csharp
foreach (var (name, spec) in options.Value.Devices)
    if (!spec.HasAnyCriteria)
        throw new InvalidOperationException(
            $"DeviceTracking:Devices:{name} sets no criteria, so it would match every device.");
```

### What the spec covers

Every `DeviceFilter` criterion except `Where(...)`, which takes a delegate and
has no data form. Categories, tags, names, USB ids, serial numbers, device and
parent ids, container ids, MAC addresses, port names, bus type, status, drive
type, USB speed, battery status, activity, physicality, and minimum resolution.

Three behaviours worth knowing before you write the JSON:

- **A misspelled or wrongly-cased member throws.** The spec is declared
  `JsonUnmappedMemberHandling.Disallow`, because the alternative is binding to an
  empty spec that silently matches every device.
- **An unparseable value throws**, naming the property. This deliberately differs
  from the fluent `WithUsbId(string, …)`, which answers a bad vendor id with a
  filter that never matches — acceptable at a C# call site, useless when the
  cause is a config file.
- **`IConfiguration` merges arrays by index.** A base file with
  `"allTags": ["Usb","Hid"]` overridden by `"allTags": ["Usb"]` yields
  `["Usb","Hid"]`. Set tag arrays in one layer.

### If you need more than one profile per device

`DeviceProfile.FromSpec` builds one profile. For the fallback-chain pattern —
several profiles tried in order until one resolves to exactly one device — bind
a list of specs and map each:

```csharp
public sealed class DeviceDefinition
{
    public Dictionary<string, DeviceFilterSpec> Profiles { get; set; } = new();
}

var tracker = new DeviceTracker(
    name,
    [.. definition.Profiles.Select(kv => DeviceProfile.FromSpec(kv.Value, kv.Key))]);
```

The profile name is stamped onto `DeviceTracker.ActiveProfile`, so diagnostics
can report which one resolved.

## 3. Hosted Service

```csharp
public sealed class DeviceTrackingService : IHostedService, IAsyncDisposable
{
    private readonly IReadOnlyList<DeviceTracker> _trackers;
    private readonly ILogger<DeviceTrackingService> _logger;

    private DeviceWatcher? _watcher;

    public DeviceTrackingService(
        IOptions<DeviceTrackingOptions> options,
        ILogger<DeviceTrackingService> logger)
    {
        _logger = logger;

        // ── Eagerly create trackers from config ──────────────────────
        // They start at ActivityStatus.Unknown — "not yet enumerated", which
        // is distinct from Absent (ADR-0056). Events can be wired here; they
        // fire once the watcher starts and initial enumeration settles.
        // Strict binding: ErrorOnUnknownConfiguration makes a misspelled key
        // throw instead of binding to a spec that quietly drops the criterion.
        // See "What the spec covers" above — this is the config-side equivalent
        // of the JSON path's unmapped-member rejection.
        _trackers = options.Value.Devices
            .Select(kv => new DeviceTracker(DeviceProfile.FromSpec(kv.Value, kv.Key)))
            .ToArray();

        foreach (var tracker in _trackers)
        {
            tracker.StateChanged += OnTrackerStateChanged;
        }
    }

    /// <summary>Look up a tracker by its config key.</summary>
    public DeviceTracker? GetTracker(string name)
        => _trackers.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All registered trackers.</summary>
    public IReadOnlyList<DeviceTracker> Trackers => _trackers;

    public async Task StartAsync(CancellationToken ct)
    {
        // ── Attach all trackers to a single watcher ──────────────────
        // One OS subscription, N in-memory filters.
        _watcher = Devices.Watch()
            .AddTrackers(_trackers);

        await _watcher.StartAsync(ct);

        foreach (var tracker in _trackers)
        {
            _logger.LogInformation(
                "Tracker '{Name}': {State} via profile '{Profile}'",
                tracker.Name,
                tracker.ActivityStatus,
                tracker.ActiveProfile?.Name ?? "(no profile)");
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            await _watcher.DisposeAsync();
            _watcher = null;
        }
        // Trackers go inert (ActivityStatus → Unknown, subscribers notified)
        // but stay wired — ready for a restart if needed.
    }

    private void OnTrackerStateChanged(object? sender, DeviceTrackerState state)
    {
        var tracker = (DeviceTracker)sender!;
        _logger.LogInformation(
            "Device '{Name}' is now {State}",
            tracker.Name,
            tracker.ActivityStatus);
    }
}
```

---

## 4. DI Registration

```csharp
// Program.cs
builder.Services.Configure<DeviceTrackingOptions>(
    builder.Configuration.GetSection("DeviceTracking"));

// Register as singleton so other services can inject it for GetTracker()
builder.Services.AddSingleton<DeviceTrackingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceTrackingService>());
```

---

## 5. Consuming from Other Services

```csharp
// Controller, Razor page, Blazor component, etc.
public class DashboardController(DeviceTrackingService tracking) : ControllerBase
{
    [HttpGet("device-status")]
    public IActionResult Status()
    {
        var mouse = tracking.GetTracker("PrimaryMouse");
        var dock = tracking.GetTracker("Dock");

        return Ok(new
        {
            MouseActive = mouse?.IsActive ?? false,
            DockActive = dock?.IsActive ?? false,
            AllDevices = tracking.Trackers.Select(t => new
            {
                t.Name,
                t.ActivityStatus,
                Device = t.Device?.Name,
                Profile = t.ActiveProfile?.Name
            })
        });
    }
}
```

### XAML Binding (WPF / MAUI)

`DeviceTracker` implements `INotifyPropertyChanged`, so it can be bound directly:

```xml
<!-- Assuming DataContext exposes a DeviceTracker property -->
<Ellipse Width="12" Height="12"
         Fill="{Binding Mouse.IsActive, Converter={StaticResource BoolToGreenRed}}" />
<TextBlock Text="{Binding Mouse.Name}" />
```

### IObservable&lt;DeviceTrackerState&gt;

For Rx consumers (bring your own `System.Reactive`). The stream carries the whole
`DeviceTrackerState`, not a bare boolean — ADR-0073 keeps the observation intact
rather than collapsing it to a verdict at the source.

```csharp
// With System.Reactive:
mouse.Where(state => state.ActivityStatus == DeviceActivityStatus.Active)
     .Throttle(TimeSpan.FromMilliseconds(500))
     .Subscribe(_ => PlayConnectedSound());

// Without System.Reactive — implement IObserver<DeviceTrackerState> directly:
mouse.Subscribe(new MyObserver());
```

---

## Key Design Points

| Concern | How it's handled |
|---|---|
| **Serialization** | `DeviceFilterSpec` is owned by the library and covers every criterion; a test enforces that. No hand-written DTO to fall behind. |
| **Eager creation** | Trackers are created in the constructor, before the watcher exists. Events are wired once. |
| **Single OS subscription** | `Devices.Watch().AddTrackers(trackers)` opens one SetupAPI/udev/IOKit subscription. Per-tracker matching happens in-memory. |
| **Survivability** | Trackers outlive the watcher. If `StopAsync` / `DisposeAsync` is called, trackers go inert but subscribers remain. `StartAsync` re-attaches them to a fresh watcher. |
| **Thread safety** | `ActivityStatus`, `Device`, and `ActiveProfile` are safe to read from any thread. Events fire on thread-pool threads; UI dispatch is your responsibility. |
| **Lookup** | `GetTracker("PrimaryMouse")` maps config keys to live state. |

---

## 6. Multi-Profile Fallback from `appsettings.json`

A single tracker can hold multiple profiles in priority order. The first profile
with exactly one active device wins. This lets you encode a "prefer hardware A,
fall back to hardware B, accept any" chain entirely in configuration.

### JSON

```json
{
  "DeviceTracking": {
    "Devices": {
      "Mouse": {
        "Profiles": {
          "MX Master 3": { "VendorId": "046D", "ProductId": "C52B" },
          "M705":        { "VendorId": "046D", "ProductId": "C534" },
          "Any Mouse":   { "Category": "Mouse" }
        }
      },
      "Keyboard": {
        "Profiles": {
          "MX Keys":      { "VendorId": "046D", "ProductId": "C52B" },
          "Any Keyboard": { "Category": "Keyboard" }
        }
      }
    }
  }
}
```

Profile keys are the profile names, so there is no `Name` field inside an entry
and no flat single-profile shorthand — a device with one profile writes one
entry under `Profiles`. Every value is a `DeviceFilterSpec`, so anything the
filter can express, the configuration can.

### DTOs

```csharp
public sealed class DeviceDefinition
{
    /// Profiles tried in order until one resolves to exactly one device.
    /// Keyed so an overlay can add a higher-priority entry without restating
    /// the base set; the key becomes DeviceProfile.Name and surfaces on
    /// DeviceTracker.ActiveProfile, so diagnostics can report which one won.
    public Dictionary<string, DeviceFilterSpec> Profiles { get; set; } = new();

    public DeviceTracker ToTracker(string name) =>
        new(name, [.. Profiles.Select(kv => DeviceProfile.FromSpec(kv.Value, kv.Key))]);
}

public sealed class DeviceTrackingOptions
{
    public Dictionary<string, DeviceDefinition> Devices { get; set; } = new();
}
```

```json
{
  "DeviceTracking": {
    "Devices": {
      "Mouse": {
        "Profiles": {
          "by-serial": { "Category": "Usb", "SerialNumber": "M-12345" },
          "by-model":  { "Category": "Usb", "VendorId": "046D", "ProductId": "C52B" }
        }
      }
    }
  }
}
```

There is no hand-written replay here and no if-ladder — `DeviceProfile.FromSpec`
owns it, so a criterion added to `DeviceFilter` cannot silently go missing from
your configuration layer.

### Reading resolved state

```csharp
var mouse = tracking.GetTracker("Mouse");

if (mouse?.IsActive is true)
    Console.WriteLine($"{mouse.ActiveProfile!.Name}: {mouse.Device!.Name}");
//  "MX Master 3: MX Master 3 Wireless Mouse"
```

`ActiveProfile.Name` tells you which profile resolved — useful for diagnostics,
telemetry, and conditional behaviour (e.g. a gaming mouse gets a different
deadzone than the fallback office mouse).

---

## See Also

- [ADR-0001: Device Tracking Handles](../adr/0001-device-tracking-handles.md) — Design rationale for `DeviceTracker`, filter aggregation, observable state model.
- [ADR-0056: Unknown initial activity state](../adr/0056-device-activity-unknown-initial-state.md) — why a fresh tracker reads `Unknown`, not `Absent`.
- [ADR-0073: Observations, not verdicts](../adr/0073-observations-not-verdicts.md) — why the observable carries state rather than a boolean.
- [ARCHITECTURE.md](../ARCHITECTURE.md) — Layering, provider contracts, filtering pipeline.
