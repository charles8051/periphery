# Periphery.Camera.Avalonia.Example

Smallest reasonable demonstration of
[`Periphery.Camera.Avalonia`](../../src/Periphery.Camera.Avalonia/) —
a single window with a camera picker bound to a
`<cam:CameraPreview>` control. The control owns the session, capture
loop, decode, and reconnect lifecycle; this app's only job is to
populate the picker.

## Run it

```bash
dotnet run --project examples/Periphery.Camera.Avalonia.Example
```

Pick a camera from the dropdown to start the preview. Replug the
camera while the preview is live to exercise the reconnect path
inside the control.

## What it demonstrates

- **`<cam:CameraPreview>`** — the packaged Avalonia control. Bind
  `Device` to a `DeviceInfo`; live preview appears.
- **`Device` binding** —
  `Device="{Binding ElementName=DevicePicker, Path=SelectedItem}"`
  is the entire wiring between picker and preview.
- **`StatusDescription` binding** —
  `Text="{Binding ElementName=Preview, Path=StatusDescription}"`
  shows the host's UI-friendly status without any code-behind switch.
- **`CameraDevice.EnumerateAsync()`** — one-call enumeration of
  attached cameras for the picker.

## Code shape

| Area | Lines | Notes |
|---|---|---|
| `MainWindow.axaml` | ~40 | Picker + preview + status text. |
| `MainWindow.axaml.cs` | ~50 | Enumerate, refresh, disconnect-via-clear-selection. |
| `App.axaml` / `App.axaml.cs` | ~15 | Avalonia bootstrap. |

Total ≈ 100 lines. Stage 1 of the same example was ≈ 250 (with the
capture loop and host wiring inline).

## What's still up to the consumer

- Picker UI (we use a `ComboBox` here; it's not part of the package).
- Hot-plug auto-refresh (manual Refresh button for now).
- Layout / styling around the preview.

## Cross-references

- [`Periphery.Camera.Avalonia`](../../src/Periphery.Camera.Avalonia/)
  — the package itself.
- [`docs/plans/periphery-camera-avalonia-preview.md`](../../docs/plans/periphery-camera-avalonia-preview.md)
  — the three-stage plan.
